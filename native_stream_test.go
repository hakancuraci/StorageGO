package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"strings"
	"sync"
	"testing"
)

// Access to these files is serialized by the owning client's opMu, including
// when a transfer operation races with client or transfer disposal.
type memoryTransferFile struct {
	reader     io.Reader
	written    bytes.Buffer
	writeLimit int
	writeErr   error
	syncErr    error
	closeErr   error
	reads      int
	writes     int
	syncs      int
	closes     int
}

func (f *memoryTransferFile) Read(p []byte) (int, error) {
	f.reads++
	if f.reader == nil {
		return 0, io.EOF
	}
	return f.reader.Read(p)
}

func (f *memoryTransferFile) Write(p []byte) (int, error) {
	f.writes++
	n := len(p)
	if f.writeLimit > 0 && n > f.writeLimit {
		n = f.writeLimit
	}
	_, _ = f.written.Write(p[:n])
	return n, f.writeErr
}

func (f *memoryTransferFile) Sync() error {
	f.syncs++
	return f.syncErr
}

func (f *memoryTransferFile) Close() error {
	f.closes++
	return f.closeErr
}

type terminalTransferReader struct {
	data []byte
	err  error
}

func (r *terminalTransferReader) Read(p []byte) (int, error) {
	n := copy(p, r.data)
	r.data = r.data[n:]
	if len(r.data) == 0 {
		return n, r.err
	}
	return n, nil
}

func newMemoryTransferClient(t *testing.T) *nativeSMBClient {
	t.Helper()
	client, err := newNativeSMBClient(NativeClientConfig{
		Server: "localhost", ShareName: "share", Username: "user", Password: "secret",
	})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(client.close)
	return client
}

func addMemoryTransfer(client *nativeSMBClient, file nativeTransferFile, readable bool) uint64 {
	client.opMu.Lock()
	defer client.opMu.Unlock()
	return registerNativeTransferLocked(client, file, readable)
}

func TestNativeTransferReadsBinaryDataAcrossChunks(t *testing.T) {
	data := make([]byte, maxNativeChunkBytes+37)
	for i := range data {
		data[i] = byte(i)
	}
	file := &memoryTransferFile{reader: bytes.NewReader(data)}
	id := addMemoryTransfer(newMemoryTransferClient(t), file, true)
	buffer := make([]byte, 1024*1024+7)
	var received bytes.Buffer
	for calls := 0; ; calls++ {
		if calls > 10 {
			t.Fatal("read never reached EOF")
		}
		result := readNativeChunk(id, buffer)
		if !result.Success || result.Error != "" {
			t.Fatalf("read = %#v", result)
		}
		if result.BytesTransferred < 0 || result.BytesTransferred > len(buffer) {
			t.Fatalf("invalid read byte count: %d", result.BytesTransferred)
		}
		_, _ = received.Write(buffer[:result.BytesTransferred])
		if result.EOF {
			break
		}
		if result.BytesTransferred == 0 {
			t.Fatal("read reported no progress without EOF")
		}
	}
	if !bytes.Equal(received.Bytes(), data) {
		t.Fatal("chunked read changed the binary content")
	}
	if file.reads < 2 {
		t.Fatal("expected multiple independent read calls")
	}
	if result := closeNativeTransfer(id, false); !result.Success {
		t.Fatalf("close = %#v", result)
	}
	if file.closes != 1 || file.syncs != 0 {
		t.Fatalf("close calls = %d, sync calls = %d", file.closes, file.syncs)
	}
}

func TestNativeTransferReadEOF(t *testing.T) {
	for _, data := range [][]byte{nil, {0, 255, 128, 1}} {
		name := "empty file"
		if len(data) > 0 {
			name = "data and EOF in same read"
		}
		t.Run(name, func(t *testing.T) {
			file := &memoryTransferFile{reader: &terminalTransferReader{data: data, err: io.EOF}}
			id := addMemoryTransfer(newMemoryTransferClient(t), file, true)
			buffer := make([]byte, 64)
			result := readNativeChunk(id, buffer)
			if !result.Success || !result.EOF || result.BytesTransferred != len(data) || result.Error != "" {
				t.Fatalf("read = %#v", result)
			}
			if !bytes.Equal(buffer[:result.BytesTransferred], data) || file.reads != 1 {
				t.Fatal("EOF read did not preserve data or was replayed")
			}
		})
	}
}

func TestNativeTransferWritesBinaryDataAcrossChunks(t *testing.T) {
	data := make([]byte, maxNativeChunkBytes+33)
	for i := range data {
		data[i] = byte(i * 17)
	}
	file := &memoryTransferFile{}
	id := addMemoryTransfer(newMemoryTransferClient(t), file, false)
	chunks := 0
	for offset := 0; offset < len(data); {
		end := min(offset+maxNativeChunkBytes, len(data))
		result := writeNativeChunk(id, data[offset:end])
		if !result.Success || result.BytesTransferred != end-offset || result.Error != "" {
			t.Fatalf("write = %#v", result)
		}
		chunks++
		offset = end
	}
	if !bytes.Equal(file.written.Bytes(), data) || file.writes != chunks {
		t.Fatal("write changed binary data or replayed a chunk")
	}
	if result := flushNativeTransfer(id); !result.Success {
		t.Fatalf("flush = %#v", result)
	}
	if result := closeNativeTransfer(id, true); !result.Success {
		t.Fatalf("close = %#v", result)
	}
	if file.syncs != 2 || file.closes != 1 {
		t.Fatalf("sync calls = %d, close calls = %d", file.syncs, file.closes)
	}
}

func TestNativeTransferChunkSizeValidation(t *testing.T) {
	for _, readable := range []bool{true, false} {
		name := "write"
		if readable {
			name = "read"
		}
		t.Run(name, func(t *testing.T) {
			file := &memoryTransferFile{reader: bytes.NewReader([]byte{42})}
			id := addMemoryTransfer(newMemoryTransferClient(t), file, readable)
			for _, buffer := range [][]byte{nil, {}, make([]byte, maxNativeChunkBytes+1)} {
				var result nativeChunkResponse
				if readable {
					result = readNativeChunk(id, buffer)
				} else {
					result = writeNativeChunk(id, buffer)
				}
				if result.Success || result.Error == "" || result.BytesTransferred != 0 {
					t.Fatalf("invalid buffer length %d: %#v", len(buffer), result)
				}
			}
			if file.reads != 0 || file.writes != 0 {
				t.Fatal("invalid buffer reached the file")
			}
			buffer := make([]byte, maxNativeChunkBytes)
			var result nativeChunkResponse
			if readable {
				result = readNativeChunk(id, buffer)
			} else {
				result = writeNativeChunk(id, buffer)
			}
			if !result.Success {
				t.Fatalf("maximum valid chunk rejected: %#v", result)
			}
		})
	}
}

func TestNativeTransferEnforcesReadWriteMode(t *testing.T) {
	client := newMemoryTransferClient(t)
	readFile, writeFile := &memoryTransferFile{}, &memoryTransferFile{}
	readID := addMemoryTransfer(client, readFile, true)
	writeID := addMemoryTransfer(client, writeFile, false)
	if result := writeNativeChunk(readID, []byte{1}); result.Success || result.Error == "" {
		t.Fatalf("write to read transfer = %#v", result)
	}
	if result := readNativeChunk(writeID, make([]byte, 1)); result.Success || result.Error == "" {
		t.Fatalf("read from write transfer = %#v", result)
	}
	if result := flushNativeTransfer(readID); !result.Success {
		t.Fatalf("read-only flush should be a no-op: %#v", result)
	}
	if result := closeNativeTransfer(readID, true); !result.Success {
		t.Fatalf("close read-only transfer = %#v", result)
	}
	if readFile.writes != 0 || writeFile.reads != 0 || readFile.syncs != 0 {
		t.Fatal("wrong-mode operation reached the file")
	}
}

func TestNativeTransferDoesNotReplayFailedChunks(t *testing.T) {
	t.Run("short write", func(t *testing.T) {
		file := &memoryTransferFile{writeLimit: 2}
		id := addMemoryTransfer(newMemoryTransferClient(t), file, false)
		result := writeNativeChunk(id, []byte{1, 2, 3, 4})
		if result.Success || result.BytesTransferred != 2 || !strings.Contains(result.Error, io.ErrShortWrite.Error()) {
			t.Fatalf("short write = %#v", result)
		}
		if file.writes != 1 || !bytes.Equal(file.written.Bytes(), []byte{1, 2}) {
			t.Fatal("short write was replayed")
		}
	})
	t.Run("partial write with error", func(t *testing.T) {
		file := &memoryTransferFile{writeLimit: 2, writeErr: errors.New("write failed")}
		id := addMemoryTransfer(newMemoryTransferClient(t), file, false)
		result := writeNativeChunk(id, []byte{1, 2, 3, 4})
		if result.Success || result.BytesTransferred != 2 || !strings.Contains(result.Error, "write failed") {
			t.Fatalf("partial write = %#v", result)
		}
		if file.writes != 1 {
			t.Fatal("failed write was replayed")
		}
	})
	t.Run("partial read with error", func(t *testing.T) {
		file := &memoryTransferFile{reader: &terminalTransferReader{data: []byte{7, 8}, err: io.ErrUnexpectedEOF}}
		id := addMemoryTransfer(newMemoryTransferClient(t), file, true)
		buffer := make([]byte, 64)
		result := readNativeChunk(id, buffer)
		if result.Success || result.EOF || result.BytesTransferred != 2 || !strings.Contains(result.Error, io.ErrUnexpectedEOF.Error()) {
			t.Fatalf("partial read = %#v", result)
		}
		if file.reads != 1 || !bytes.Equal(buffer[:2], []byte{7, 8}) {
			t.Fatal("failed read was replayed or data was discarded")
		}
	})
}

func TestNativeTransferCloseReleasesAfterErrors(t *testing.T) {
	for _, test := range []struct {
		name     string
		syncErr  error
		closeErr error
	}{
		{name: "sync error", syncErr: errors.New("sync failed")},
		{name: "close error", closeErr: errors.New("close failed")},
		{name: "sync and close error", syncErr: errors.New("sync failed"), closeErr: errors.New("close failed")},
	} {
		t.Run(test.name, func(t *testing.T) {
			file := &memoryTransferFile{syncErr: test.syncErr, closeErr: test.closeErr}
			id := addMemoryTransfer(newMemoryTransferClient(t), file, false)
			result := closeNativeTransfer(id, true)
			if result.Success || result.Error == "" {
				t.Fatalf("close = %#v", result)
			}
			if file.syncs != 1 || file.closes != 1 {
				t.Fatalf("sync calls = %d, close calls = %d", file.syncs, file.closes)
			}
			if result := closeNativeTransfer(id, true); result.Success || result.Error == "" {
				t.Fatalf("second close = %#v", result)
			}
			if result := writeNativeChunk(id, []byte{1}); result.Success || result.Error == "" {
				t.Fatalf("write after failed close = %#v", result)
			}
			if file.closes != 1 || file.writes != 0 {
				t.Fatal("released transfer was reused")
			}
		})
	}
}

func TestNativeTransferFlushReportsSyncFailure(t *testing.T) {
	file := &memoryTransferFile{syncErr: errors.New("sync failed")}
	id := addMemoryTransfer(newMemoryTransferClient(t), file, false)
	result := flushNativeTransfer(id)
	if result.Success || !strings.Contains(result.Error, "sync failed") || file.syncs != 1 {
		t.Fatalf("flush = %#v, sync calls = %d", result, file.syncs)
	}
}

func TestNativeTransferRejectsUnknownHandle(t *testing.T) {
	id := ^uint64(0)
	for _, result := range []nativeChunkResponse{
		readNativeChunk(id, make([]byte, 1)),
		writeNativeChunk(id, []byte{1}),
	} {
		if result.Success || result.Error == "" || result.BytesTransferred != 0 {
			t.Fatalf("unknown transfer chunk = %#v", result)
		}
	}
	for _, result := range []nativeTransferResponse{
		flushNativeTransfer(id), closeNativeTransfer(id, false),
	} {
		if result.Success || result.Error == "" {
			t.Fatalf("unknown transfer control = %#v", result)
		}
	}
}

func TestNativeTransferOpenValidatesBeforeConnecting(t *testing.T) {
	created := createNativeClient(`{"server":"localhost","shareName":"share","username":"user"}`)
	if !created.Success {
		t.Fatal(created.Error)
	}
	t.Cleanup(func() { closeNativeClient(created.ClientID) })
	for _, remotePath := range []string{"", " \t ", "file\x00.txt"} {
		for _, result := range []nativeTransferResponse{
			openNativeRead(created.ClientID, remotePath),
			openNativeWrite(created.ClientID, remotePath, false),
		} {
			if result.Success || result.Error == "" || result.TransferID != 0 {
				t.Fatalf("invalid path %q: %#v", remotePath, result)
			}
		}
	}
	if result := openNativeRead(^uint64(0), "file.txt"); result.Success || result.Error == "" {
		t.Fatalf("unknown client open = %#v", result)
	}
}

func TestNativeControlJSONHandlesFailureWithoutLeakingPanic(t *testing.T) {
	for _, call := range []func() any{
		func() any { panic("secret credential or application content") },
		func() any { return make(chan int) },
	} {
		encoded := nativeControlJSON(call)
		var result nativeTransferResponse
		if err := json.Unmarshal([]byte(encoded), &result); err != nil {
			t.Fatalf("error response is not JSON: %v", err)
		}
		if result.Success || result.Error == "" || strings.Contains(encoded, "secret") {
			t.Fatalf("unsafe error response: %q", encoded)
		}
	}
}

func TestNativeClientCloseReleasesEveryTransfer(t *testing.T) {
	created := createNativeClient(`{"server":"localhost","shareName":"share","username":"user","password":"secret"}`)
	if !created.Success {
		t.Fatal(created.Error)
	}
	t.Cleanup(func() { closeNativeClient(created.ClientID) })
	client, found := getNativeClient(created.ClientID)
	if !found {
		t.Fatal("created client not registered")
	}
	readFile := &memoryTransferFile{closeErr: errors.New("read close failed")}
	writeFile := &memoryTransferFile{}
	readID := addMemoryTransfer(client, readFile, true)
	writeID := addMemoryTransfer(client, writeFile, false)
	if result := closeNativeClient(created.ClientID); !result.Success {
		t.Fatalf("client close = %#v", result)
	}
	if readFile.closes != 1 || writeFile.closes != 1 {
		t.Fatalf("client close left transfers open: read=%d, write=%d", readFile.closes, writeFile.closes)
	}
	if result := readNativeChunk(readID, make([]byte, 1)); result.Success || result.Error == "" {
		t.Fatalf("read after client close = %#v", result)
	}
	if result := writeNativeChunk(writeID, []byte{1}); result.Success || result.Error == "" {
		t.Fatalf("write after client close = %#v", result)
	}
	if client.config.Password != "" {
		t.Fatal("client retained its password after disposal")
	}
}

func TestNativeTransferConcurrentOperationsAndClose(t *testing.T) {
	client := newMemoryTransferClient(t)
	file := &memoryTransferFile{}
	id := addMemoryTransfer(client, file, false)
	if result := writeNativeChunk(id, []byte{42}); !result.Success {
		t.Fatalf("initial write = %#v", result)
	}
	start := make(chan struct{})
	var workers sync.WaitGroup
	for worker := 0; worker < 8; worker++ {
		workers.Add(1)
		go func() {
			defer workers.Done()
			<-start
			for chunk := 0; chunk < 32; chunk++ {
				result := writeNativeChunk(id, []byte{1, 2, 3})
				if !result.Success && result.Error == "" {
					t.Errorf("failed concurrent write has no error: %#v", result)
				}
				flush := flushNativeTransfer(id)
				if !flush.Success && flush.Error == "" {
					t.Errorf("failed concurrent flush has no error: %#v", flush)
				}
			}
		}()
	}
	workers.Add(2)
	go func() {
		defer workers.Done()
		<-start
		closeNativeTransfer(id, true)
	}()
	go func() {
		defer workers.Done()
		<-start
		client.close()
	}()
	close(start)
	workers.Wait()
	if file.closes != 1 {
		t.Fatalf("concurrent close called file.Close %d times", file.closes)
	}
	if result := writeNativeChunk(id, []byte{9}); result.Success || result.Error == "" {
		t.Fatalf("write after concurrent disposal = %#v", result)
	}
}
