package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path"
	"strings"
	"sync"
	"sync/atomic"
)

const (
	nativeABIVersion    = 3
	maxNativeChunkBytes = 4 * 1024 * 1024
)

// Only control information crosses the ABI as JSON. File bytes are read/written
// directly in the caller-owned buffer, which is never retained after a call.
type nativeTransferResponse struct {
	Success    bool   `json:"success"`
	TransferID uint64 `json:"transferId,omitempty"`
	FileName   string `json:"fileName,omitempty"`
	FileSize   int64  `json:"fileSize"`
	MimeType   string `json:"mimeType,omitempty"`
	Error      string `json:"error,omitempty"`
}

type nativeChunkResponse struct {
	Success          bool   `json:"success"`
	BytesTransferred int    `json:"bytesTransferred"`
	EOF              bool   `json:"eof"`
	Error            string `json:"error,omitempty"`
}

type nativeTransferFile interface {
	io.Reader
	io.Writer
	io.Closer
	Sync() error
}

// All mutable transfer state and file I/O are protected by client.opMu. Registry
// locks are released before taking opMu, including when Close races a chunk.
type nativeTransfer struct {
	id       uint64
	client   *nativeSMBClient
	file     nativeTransferFile
	readable bool
	closed   bool
}

var nativeTransfers = struct {
	sync.RWMutex
	items map[uint64]*nativeTransfer
}{items: make(map[uint64]*nativeTransfer)}

var nextNativeTransferID atomic.Uint64

func registerNativeTransferLocked(c *nativeSMBClient, file nativeTransferFile, readable bool) uint64 {
	id := nextNativeTransferID.Add(1)
	transfer := &nativeTransfer{id: id, client: c, file: file, readable: readable}
	if c.transfers == nil {
		c.transfers = make(map[uint64]*nativeTransfer)
	}
	c.transfers[id] = transfer
	nativeTransfers.Lock()
	nativeTransfers.items[id] = transfer
	nativeTransfers.Unlock()
	return id
}

func unregisterNativeTransfer(id uint64) {
	nativeTransfers.Lock()
	delete(nativeTransfers.items, id)
	nativeTransfers.Unlock()
}

func getNativeTransfer(id uint64) (*nativeTransfer, bool) {
	nativeTransfers.RLock()
	transfer, found := nativeTransfers.items[id]
	nativeTransfers.RUnlock()
	return transfer, found
}

func openNativeRead(clientID uint64, remotePath string) nativeTransferResponse {
	return openNativeTransfer(clientID, remotePath, true, false)
}

func openNativeWrite(clientID uint64, remotePath string, overwrite bool) nativeTransferResponse {
	return openNativeTransfer(clientID, remotePath, false, overwrite)
}

func openNativeTransfer(clientID uint64, remotePath string, readable, overwrite bool) nativeTransferResponse {
	return openNativeTransferContext(context.Background(), clientID, remotePath, readable, overwrite)
}

func openNativeTransferContext(ctx context.Context, clientID uint64, remotePath string, readable, overwrite bool) nativeTransferResponse {
	if strings.TrimSpace(remotePath) == "" || strings.ContainsRune(remotePath, '\x00') {
		return nativeTransferResponse{Error: "path boş olamaz veya NULL karakteri içeremez"}
	}
	client, found := getNativeClient(clientID)
	if !found {
		return nativeTransferResponse{Error: "client bulunamadı veya kapatıldı"}
	}
	client.opMu.Lock()
	defer client.opMu.Unlock()
	if err := ctx.Err(); err != nil {
		return nativeTransferResponse{Error: err.Error()}
	}

	share, err := client.connectLockedContext(ctx)
	if err != nil {
		return nativeTransferResponse{Error: err.Error()}
	}
	flags := os.O_RDONLY
	if !readable {
		flags = os.O_CREATE | os.O_WRONLY | os.O_EXCL
		if overwrite {
			flags = os.O_CREATE | os.O_WRONLY | os.O_TRUNC
		}
	}
	file, err := share.OpenFile(remotePath, flags, 0o644)
	if err != nil {
		// Do not reset a session underneath unrelated open transfers, and do not
		// retry a create/truncate whose outcome is unknown after a network error.
		if len(client.transfers) == 0 {
			client.resetLocked()
		}
		return nativeTransferResponse{Error: fmt.Sprintf("dosya açılamadı: %v", err)}
	}

	info, err := file.Stat()
	if err != nil || info.IsDir() {
		_ = file.Close()
		if err != nil {
			return nativeTransferResponse{Error: fmt.Sprintf("dosya bilgisi alınamadı: %v", err)}
		}
		return nativeTransferResponse{Error: "path bir dosya olmalıdır"}
	}

	// Sniff at most 512 bytes, then rewind before exposing a forward-only stream.
	// Metadata remains consistent across Windows and Linux path separators.
	var sniff []byte
	if readable {
		var header [512]byte
		n, readErr := file.Read(header[:])
		if readErr != nil && !errors.Is(readErr, io.EOF) {
			_ = file.Close()
			return nativeTransferResponse{Error: fmt.Sprintf("dosya başlığı okunamadı: %v", readErr)}
		}
		sniff = header[:n]
		if _, err := file.Seek(0, io.SeekStart); err != nil {
			_ = file.Close()
			return nativeTransferResponse{Error: fmt.Sprintf("dosya konumu sıfırlanamadı: %v", err)}
		}
	}
	id := registerNativeTransferLocked(client, file, readable)
	return nativeTransferResponse{
		Success: true, TransferID: id, FileSize: info.Size(),
		FileName: path.Base(strings.ReplaceAll(remotePath, `\`, "/")),
		MimeType: getMimeType(remotePath, sniff),
	}
}

func validateNativeChunk(buffer []byte) error {
	if len(buffer) < 1 || len(buffer) > maxNativeChunkBytes {
		return fmt.Errorf("chunk boyutu 1 ile %d byte arasında olmalıdır", maxNativeChunkBytes)
	}
	return nil
}

func readNativeChunk(transferID uint64, buffer []byte) nativeChunkResponse {
	return readNativeChunkAsync(context.Background(), transferID, buffer)
}

func readNativeChunkAsync(ctx context.Context, transferID uint64, buffer []byte) nativeChunkResponse {
	if err := validateNativeChunk(buffer); err != nil {
		return nativeChunkResponse{Error: err.Error()}
	}
	transfer, found := getNativeTransfer(transferID)
	if !found {
		return nativeChunkResponse{Error: "transfer bulunamadı veya kapatıldı"}
	}
	transfer.client.opMu.Lock()
	defer transfer.client.opMu.Unlock()
	if err := ctx.Err(); err != nil {
		return nativeChunkResponse{Error: err.Error()}
	}
	if transfer.closed || transfer.client.closed {
		return nativeChunkResponse{Error: "transfer kapatıldı"}
	}
	if !transfer.readable {
		return nativeChunkResponse{Error: "transfer okuma için açılmadı"}
	}

	n, err := transfer.file.Read(buffer)
	if n < 0 || n > len(buffer) {
		return nativeChunkResponse{Error: "geçersiz native okuma uzunluğu"}
	}
	if err != nil && !errors.Is(err, io.EOF) {
		return nativeChunkResponse{BytesTransferred: n, Error: fmt.Sprintf("chunk okunamadı: %v", err)}
	}
	if n == 0 && err == nil {
		return nativeChunkResponse{Error: io.ErrNoProgress.Error()}
	}
	return nativeChunkResponse{Success: true, BytesTransferred: n, EOF: errors.Is(err, io.EOF)}
}

func writeNativeChunk(transferID uint64, buffer []byte) nativeChunkResponse {
	return writeNativeChunkAsync(context.Background(), transferID, buffer)
}

func writeNativeChunkAsync(ctx context.Context, transferID uint64, buffer []byte) nativeChunkResponse {
	if err := validateNativeChunk(buffer); err != nil {
		return nativeChunkResponse{Error: err.Error()}
	}
	transfer, found := getNativeTransfer(transferID)
	if !found {
		return nativeChunkResponse{Error: "transfer bulunamadı veya kapatıldı"}
	}
	transfer.client.opMu.Lock()
	defer transfer.client.opMu.Unlock()
	if err := ctx.Err(); err != nil {
		return nativeChunkResponse{Error: err.Error()}
	}
	if transfer.closed || transfer.client.closed {
		return nativeChunkResponse{Error: "transfer kapatıldı"}
	}
	if transfer.readable {
		return nativeChunkResponse{Error: "transfer yazma için açılmadı"}
	}

	n, err := transfer.file.Write(buffer)
	if n < 0 || n > len(buffer) {
		return nativeChunkResponse{Error: "geçersiz native yazma uzunluğu"}
	}
	if err == nil && n != len(buffer) {
		err = io.ErrShortWrite
	}
	if err != nil {
		return nativeChunkResponse{BytesTransferred: n, Error: fmt.Sprintf("chunk yazılamadı: %v", err)}
	}
	return nativeChunkResponse{Success: true, BytesTransferred: n}
}

func flushNativeTransfer(transferID uint64) nativeTransferResponse {
	return flushNativeTransferAsync(context.Background(), transferID)
}

func flushNativeTransferAsync(ctx context.Context, transferID uint64) nativeTransferResponse {
	transfer, found := getNativeTransfer(transferID)
	if !found {
		return nativeTransferResponse{Error: "transfer bulunamadı veya kapatıldı"}
	}
	transfer.client.opMu.Lock()
	defer transfer.client.opMu.Unlock()
	if err := ctx.Err(); err != nil {
		return nativeTransferResponse{Error: err.Error()}
	}
	if transfer.closed || transfer.client.closed {
		return nativeTransferResponse{Error: "transfer kapatıldı"}
	}
	if !transfer.readable {
		if err := transfer.file.Sync(); err != nil {
			return nativeTransferResponse{Error: fmt.Sprintf("dosya senkronize edilemedi: %v", err)}
		}
	}
	return nativeTransferResponse{Success: true}
}

func closeNativeTransfer(transferID uint64, forceSync bool) nativeTransferResponse {
	transfer, found := getNativeTransfer(transferID)
	if !found {
		return nativeTransferResponse{Error: "transfer bulunamadı veya kapatıldı"}
	}
	transfer.client.opMu.Lock()
	defer transfer.client.opMu.Unlock()
	if transfer.closed {
		return nativeTransferResponse{Error: "transfer kapatıldı"}
	}

	// Release ownership even when Sync/Close reports an error. Never replay writes.
	transfer.closed = true
	delete(transfer.client.transfers, transferID)
	unregisterNativeTransfer(transferID)
	var syncErr error
	if forceSync && !transfer.readable {
		syncErr = transfer.file.Sync()
	}
	err := errors.Join(syncErr, transfer.file.Close())
	if err != nil {
		return nativeTransferResponse{Error: fmt.Sprintf("dosya kapatılamadı: %v", err)}
	}
	return nativeTransferResponse{Success: true}
}

// Serialize only ABI control responses. Do not include recovered panic values:
// third-party panic messages could contain application data or credentials.
func nativeControlJSON(call func() any) (result string) {
	defer func() {
		if recover() != nil {
			result = `{"success":false,"error":"beklenmeyen native hata"}`
		}
	}()
	encoded, err := json.Marshal(call())
	if err != nil {
		return `{"success":false,"error":"native yanıt kodlanamadı"}`
	}
	return string(encoded)
}
