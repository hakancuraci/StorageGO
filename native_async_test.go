package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

type asyncBlockingTransferFile struct {
	memoryTransferFile
	entered    chan struct{}
	release    chan struct{}
	blockRead  bool
	blockWrite bool
	once       sync.Once
}

func (f *asyncBlockingTransferFile) wait() {
	f.once.Do(func() { close(f.entered) })
	<-f.release
}

func (f *asyncBlockingTransferFile) Read(buffer []byte) (int, error) {
	if f.blockRead {
		f.wait()
	}
	return f.memoryTransferFile.Read(buffer)
}

func (f *asyncBlockingTransferFile) Write(buffer []byte) (int, error) {
	if f.blockWrite {
		f.wait()
	}
	return f.memoryTransferFile.Write(buffer)
}

type asyncTestResult struct {
	response string
	data     []byte
}

type asyncTestResponse struct {
	Success          bool   `json:"success"`
	Cancelled        bool   `json:"cancelled"`
	BytesTransferred int    `json:"bytesTransferred"`
	Error            string `json:"error"`
}

func asyncTestCallback(t *testing.T) (func(string, []byte), <-chan asyncTestResult) {
	t.Helper()
	results := make(chan asyncTestResult, 2)
	var calls atomic.Int32
	return func(response string, data []byte) {
		if calls.Add(1) != 1 {
			t.Error("native async operation completed more than once")
		}
		// Callback buffers are borrowed; consumers must copy before returning.
		results <- asyncTestResult{response: response, data: append([]byte(nil), data...)}
	}, results
}

func receiveAsyncTestResult(t *testing.T, results <-chan asyncTestResult) (asyncTestResponse, []byte) {
	t.Helper()
	select {
	case result := <-results:
		var response asyncTestResponse
		if err := json.Unmarshal([]byte(result.response), &response); err != nil {
			t.Fatalf("async completion is not JSON: %v", err)
		}
		return response, result.data
	case <-time.After(5 * time.Second):
		t.Fatal("async operation did not complete")
		return asyncTestResponse{}, nil
	}
}

func TestNativeAsyncBeginReturnsBeforeBlockedReadCompletes(t *testing.T) {
	file := &asyncBlockingTransferFile{
		memoryTransferFile: memoryTransferFile{reader: bytes.NewReader([]byte{0, 128, 255, 7})},
		entered:            make(chan struct{}), release: make(chan struct{}), blockRead: true,
	}
	var release sync.Once
	defer release.Do(func() { close(file.release) })
	id := addMemoryTransfer(newMemoryTransferClient(t), file, true)
	callback, results := asyncTestCallback(t)
	returned := make(chan uint64, 1)
	go func() {
		returned <- beginNativeAsync(fmt.Sprintf(`{"action":"read","transferId":%d}`, id), nil, 64, callback)
	}()
	var operationID uint64
	select {
	case operationID = <-returned:
		if operationID == 0 {
			t.Fatal("valid async read was rejected")
		}
	case <-time.After(2 * time.Second):
		t.Fatal("Begin blocked its caller on file I/O")
	}
	select {
	case <-file.entered:
	case <-time.After(2 * time.Second):
		t.Fatal("async read never started")
	}
	select {
	case <-results:
		t.Fatal("read completed before its I/O finished")
	default:
	}
	release.Do(func() { close(file.release) })
	response, data := receiveAsyncTestResult(t, results)
	if !response.Success || response.Cancelled || response.BytesTransferred != 4 || !bytes.Equal(data, []byte{0, 128, 255, 7}) {
		t.Fatalf("async read response = %#v, data = %v", response, data)
	}
	cancelNativeAsync(operationID) // A completed ID must be harmless to cancel.
}

func TestNativeAsyncWriteCopiesInputBeforeReturning(t *testing.T) {
	client := newMemoryTransferClient(t)
	file := &memoryTransferFile{}
	id := addMemoryTransfer(client, file, false)
	callback, results := asyncTestCallback(t)
	source := []byte{0, 128, 255, 7}
	client.opMu.Lock()
	operationID := beginNativeAsync(fmt.Sprintf(`{"action":"write","transferId":%d}`, id), source, len(source), callback)
	for i := range source {
		source[i] = 99
	}
	client.opMu.Unlock()
	if operationID == 0 {
		t.Fatal("valid async write was rejected")
	}
	response, _ := receiveAsyncTestResult(t, results)
	if !response.Success || response.BytesTransferred != len(source) || !bytes.Equal(file.written.Bytes(), []byte{0, 128, 255, 7}) {
		t.Fatalf("async write = %#v, file = %v", response, file.written.Bytes())
	}
}

func TestNativeAsyncInflightCancellationWaitsForIO(t *testing.T) {
	file := &asyncBlockingTransferFile{
		memoryTransferFile: memoryTransferFile{reader: bytes.NewReader([]byte{1, 2, 3})},
		entered:            make(chan struct{}), release: make(chan struct{}), blockRead: true,
	}
	var release sync.Once
	defer release.Do(func() { close(file.release) })
	id := addMemoryTransfer(newMemoryTransferClient(t), file, true)
	callback, results := asyncTestCallback(t)
	operationID := beginNativeAsync(fmt.Sprintf(`{"action":"read","transferId":%d}`, id), nil, 16, callback)
	if operationID == 0 {
		t.Fatal("valid async read was rejected")
	}
	select {
	case <-file.entered:
	case <-time.After(2 * time.Second):
		t.Fatal("async read never started")
	}
	cancelNativeAsync(operationID)
	select {
	case <-results:
		t.Fatal("cancellation completed while file still owned the operation's buffer")
	default:
	}
	release.Do(func() { close(file.release) })
	response, _ := receiveAsyncTestResult(t, results)
	if response.Success || !response.Cancelled || file.reads != 1 {
		t.Fatalf("in-flight cancelled read = %#v, read calls = %d", response, file.reads)
	}
}

func TestNativeAsyncQueuedCancellationSkipsFileIO(t *testing.T) {
	for _, action := range []string{"read", "write", "flush"} {
		t.Run(action, func(t *testing.T) {
			client := newMemoryTransferClient(t)
			file := &memoryTransferFile{reader: bytes.NewReader([]byte{1})}
			id := addMemoryTransfer(client, file, action == "read")
			callback, results := asyncTestCallback(t)
			client.opMu.Lock()
			operationID := beginNativeAsync(fmt.Sprintf(`{"action":%q,"transferId":%d}`, action, id), []byte{1}, 1, callback)
			cancelNativeAsync(operationID)
			client.opMu.Unlock()
			if operationID == 0 {
				t.Fatal("valid operation was rejected")
			}
			response, _ := receiveAsyncTestResult(t, results)
			if response.Success || !response.Cancelled || file.reads != 0 || file.writes != 0 || file.syncs != 0 {
				t.Fatalf("queued cancellation = %#v, calls: read=%d write=%d sync=%d", response, file.reads, file.writes, file.syncs)
			}
		})
	}
}

func TestNativeAsyncCancelledCloseStillReleasesTransfer(t *testing.T) {
	client := newMemoryTransferClient(t)
	file := &memoryTransferFile{}
	id := addMemoryTransfer(client, file, false)
	callback, results := asyncTestCallback(t)
	client.opMu.Lock()
	operationID := beginNativeAsync(fmt.Sprintf(`{"action":"closeTransfer","transferId":%d}`, id), nil, 0, callback)
	cancelNativeAsync(operationID)
	client.opMu.Unlock()
	if operationID == 0 {
		t.Fatal("valid close operation was rejected")
	}
	response, _ := receiveAsyncTestResult(t, results)
	if !response.Cancelled || file.closes != 1 {
		t.Fatalf("cancelled close = %#v, close calls = %d", response, file.closes)
	}
	if result := writeNativeChunk(id, []byte{1}); result.Success {
		t.Fatal("cancelled close retained a usable transfer")
	}
}

func TestNativeAsyncCloseClientReleasesTransfersEvenWhenCancelled(t *testing.T) {
	created := createNativeClient(`{"server":"localhost","shareName":"share","username":"user","password":"secret"}`)
	if !created.Success {
		t.Fatal(created.Error)
	}
	t.Cleanup(func() { closeNativeClient(created.ClientID) })
	client, _ := getNativeClient(created.ClientID)
	file := &memoryTransferFile{}
	addMemoryTransfer(client, file, false)
	callback, results := asyncTestCallback(t)
	client.opMu.Lock()
	operationID := beginNativeAsync(fmt.Sprintf(`{"action":"closeClient","clientId":%d}`, created.ClientID), nil, 0, callback)
	cancelNativeAsync(operationID)
	client.opMu.Unlock()
	if operationID == 0 {
		t.Fatal("valid client close was rejected")
	}
	response, _ := receiveAsyncTestResult(t, results)
	if !response.Cancelled || file.closes != 1 || client.config.Password != "" {
		t.Fatalf("cancelled client close = %#v, close calls = %d", response, file.closes)
	}
	if _, found := getNativeClient(created.ClientID); found {
		t.Fatal("cancelled client close retained client registry entry")
	}
}

func TestNativeAsyncInvalidRequestCompletesAsFailure(t *testing.T) {
	for _, request := range []string{"{", `{"action":"unknown"}`, `{"action":"read","transferId":18446744073709551615}`} {
		callback, results := asyncTestCallback(t)
		if id := beginNativeAsync(request, nil, 1, callback); id == 0 {
			t.Fatal("request validation must complete through its accepted callback")
		}
		response, _ := receiveAsyncTestResult(t, results)
		if response.Success || response.Cancelled || response.Error == "" {
			t.Fatalf("invalid request response = %#v", response)
		}
	}
	if id := beginNativeAsync(`{"action":"read"}`, nil, 1, nil); id != 0 {
		t.Fatal("a missing callback must be rejected")
	}
}

func TestNativeAsyncCancelledOpenReleasesNewTransfer(t *testing.T) {
	client := newMemoryTransferClient(t)
	file := &memoryTransferFile{}
	entered := make(chan uint64, 1)
	release := make(chan struct{})
	var releaseOnce sync.Once
	defer releaseOnce.Do(func() { close(release) })
	original := nativeAsyncOpenRead
	defer func() { nativeAsyncOpenRead = original }()
	nativeAsyncOpenRead = func(context.Context, uint64, string) nativeTransferResponse {
		id := addMemoryTransfer(client, file, true)
		entered <- id
		<-release
		return nativeTransferResponse{Success: true, TransferID: id}
	}
	callback, results := asyncTestCallback(t)
	operationID := beginNativeAsync(`{"action":"openRead","path":"file.bin"}`, nil, 0, callback)
	if operationID == 0 {
		t.Fatal("valid open was rejected")
	}
	var transferID uint64
	select {
	case transferID = <-entered:
	case <-time.After(2 * time.Second):
		t.Fatal("open did not start")
	}
	cancelNativeAsync(operationID)
	releaseOnce.Do(func() { close(release) })
	response, _ := receiveAsyncTestResult(t, results)
	if !response.Cancelled || response.Success || file.closes != 1 {
		t.Fatalf("cancelled open = %#v, close calls = %d", response, file.closes)
	}
	if _, found := getNativeTransfer(transferID); found {
		t.Fatal("cancelled open retained an unowned transfer")
	}
}

func TestNativeAsyncAdmissionIsBoundedAndCancelledSlotsAreReleased(t *testing.T) {
	client := newMemoryTransferClient(t)
	file := &memoryTransferFile{}
	id := addMemoryTransfer(client, file, false)
	results := make(chan asyncTestResult, maxNativeAsyncOperations+1)
	callback := func(response string, data []byte) { results <- asyncTestResult{response: response} }
	ids := make([]uint64, 0, maxNativeAsyncOperations)
	client.opMu.Lock()
	locked := true
	defer func() {
		for _, operationID := range ids {
			cancelNativeAsync(operationID)
		}
		if locked {
			client.opMu.Unlock()
		}
	}()
	request := fmt.Sprintf(`{"action":"flush","transferId":%d}`, id)
	for i := 0; i < maxNativeAsyncOperations; i++ {
		operationID := beginNativeAsync(request, nil, 0, callback)
		if operationID == 0 {
			t.Fatalf("admission rejected operation %d before capacity %d", i, maxNativeAsyncOperations)
		}
		ids = append(ids, operationID)
	}
	var rejectedCallback atomic.Bool
	if overflow := beginNativeAsync(request, nil, 0, func(string, []byte) { rejectedCallback.Store(true) }); overflow != 0 {
		t.Fatalf("admitted operation beyond limit %d", maxNativeAsyncOperations)
	}
	for _, operationID := range ids {
		cancelNativeAsync(operationID)
	}
	client.opMu.Unlock()
	locked = false
	for range ids {
		response, _ := receiveAsyncTestResult(t, results)
		if response.Success || !response.Cancelled {
			t.Fatalf("queued operation did not cancel: %#v", response)
		}
	}
	if rejectedCallback.Load() || file.syncs != 0 {
		t.Fatalf("rejected callback fired or queued work performed I/O: callback=%v sync calls=%d", rejectedCallback.Load(), file.syncs)
	}
	callbackAfterDrain, afterDrain := asyncTestCallback(t)
	if operationID := beginNativeAsync(request, nil, 0, callbackAfterDrain); operationID == 0 {
		t.Fatal("capacity was not released after cancelled operations completed")
	}
	response, _ := receiveAsyncTestResult(t, afterDrain)
	if !response.Success || file.syncs != 1 {
		t.Fatalf("operation after drain = %#v, sync calls = %d", response, file.syncs)
	}
}
