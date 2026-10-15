//go:build storagego_interop_fixture

// This fixture is compiled only by tests/run-interop.ps1 into an isolated test
// library. It exercises the production chunk registry with local files instead
// of requiring SMB credentials or modifying the shipped native library.
package main

/*
#include <stdlib.h>
#include <stdint.h>
*/
import "C"

import (
	"context"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"time"
	"unsafe"
)

func init() {
	nativeAsyncOpenRead = func(ctx context.Context, clientID uint64, name string) nativeTransferResponse {
		return fixtureOpenContext(ctx, clientID, name, true, false)
	}
	nativeAsyncOpenWrite = func(ctx context.Context, clientID uint64, name string, overwrite bool) nativeTransferResponse {
		return fixtureOpenContext(ctx, clientID, name, false, overwrite)
	}
}

var fixtureSlowCalls atomic.Uint64
var fixtureIOGate = struct {
	sync.Mutex
	wait chan struct{}
}{}

func fixtureWait(name string) {
	if strings.HasSuffix(name, ".slow") {
		fixtureSlowCalls.Add(1)
		time.Sleep(200 * time.Millisecond)
	}
	if strings.HasSuffix(name, ".blocked") {
		fixtureIOGate.Lock()
		wait := fixtureIOGate.wait
		fixtureIOGate.Unlock()
		fixtureSlowCalls.Add(1)
		if wait != nil {
			<-wait
		}
	}
}

type fixtureFile struct {
	*os.File
	name string
}

func (f *fixtureFile) Read(p []byte) (int, error) {
	fixtureWait(f.name)
	return f.File.Read(p)
}

func (f *fixtureFile) Write(p []byte) (int, error) {
	fixtureWait(f.name)
	if strings.HasSuffix(f.name, ".writefail") {
		n, err := f.File.Write(p[:min(2, len(p))])
		if err != nil {
			return n, err
		}
		return n, errors.New("fixture partial write failure")
	}
	return f.File.Write(p)
}

func (f *fixtureFile) Sync() error {
	if strings.HasSuffix(f.name, ".syncfail") {
		return errors.New("fixture sync failure")
	}
	return f.File.Sync()
}

func (f *fixtureFile) Close() error {
	if strings.HasSuffix(f.name, ".slowclose") {
		fixtureSlowCalls.Add(1)
		time.Sleep(200 * time.Millisecond)
	}
	err := f.File.Close()
	if strings.HasSuffix(f.name, ".closefail") {
		return errors.Join(err, errors.New("fixture close failure"))
	}
	return err
}

func fixtureOpen(clientID uint64, name string, readable, overwrite bool) nativeTransferResponse {
	return fixtureOpenContext(context.Background(), clientID, name, readable, overwrite)
}

func fixtureOpenContext(ctx context.Context, clientID uint64, name string, readable, overwrite bool) nativeTransferResponse {
	if strings.TrimSpace(name) == "" || strings.ContainsRune(name, '\x00') {
		return nativeTransferResponse{Error: "invalid fixture path"}
	}
	client, found := getNativeClient(clientID)
	if !found {
		return nativeTransferResponse{Error: "fixture client not found"}
	}
	client.opMu.Lock()
	defer client.opMu.Unlock()
	if ctx.Err() != nil {
		return nativeTransferResponse{Error: "fixture open cancelled before I/O"}
	}
	if client.closed {
		return nativeTransferResponse{Error: "fixture client closed"}
	}
	if strings.HasSuffix(name, ".slowopen") {
		fixtureSlowCalls.Add(1)
		time.Sleep(200 * time.Millisecond)
	}
	flags := os.O_RDONLY
	if !readable {
		flags = os.O_CREATE | os.O_WRONLY | os.O_EXCL
		if overwrite {
			flags = os.O_CREATE | os.O_WRONLY | os.O_TRUNC
		}
	}
	file, err := os.OpenFile(name, flags, 0o600)
	if err != nil {
		return nativeTransferResponse{Error: err.Error()}
	}
	info, err := file.Stat()
	if err != nil || info.IsDir() {
		_ = file.Close()
		return nativeTransferResponse{Error: "fixture path is not a regular file"}
	}
	var header [512]byte
	var sniff []byte
	if readable {
		n, err := file.ReadAt(header[:], 0)
		if err != nil && !errors.Is(err, io.EOF) {
			_ = file.Close()
			return nativeTransferResponse{Error: err.Error()}
		}
		sniff = header[:n]
	}
	id := registerNativeTransferLocked(client, &fixtureFile{File: file, name: name}, readable)
	return nativeTransferResponse{
		Success: true, TransferID: id, FileName: filepath.Base(name),
		FileSize: info.Size(), MimeType: getMimeType(name, sniff),
	}
}

//export StorageGO_FixtureIsLocalFile
func StorageGO_FixtureIsLocalFile() C.uint32_t { return 1 }

//export StorageGO_FixtureClientCount
func StorageGO_FixtureClientCount() C.uint64_t {
	nativeClients.RLock()
	defer nativeClients.RUnlock()
	return C.uint64_t(len(nativeClients.items))
}

//export StorageGO_FixtureTransferCount
func StorageGO_FixtureTransferCount() C.uint64_t {
	nativeTransfers.RLock()
	defer nativeTransfers.RUnlock()
	return C.uint64_t(len(nativeTransfers.items))
}

//export StorageGO_FixtureSlowCallCount
func StorageGO_FixtureSlowCallCount() C.uint64_t { return C.uint64_t(fixtureSlowCalls.Load()) }

//export StorageGO_FixtureArmIOGate
func StorageGO_FixtureArmIOGate() {
	fixtureIOGate.Lock()
	defer fixtureIOGate.Unlock()
	if fixtureIOGate.wait != nil {
		close(fixtureIOGate.wait)
	}
	fixtureIOGate.wait = make(chan struct{})
}

//export StorageGO_FixtureReleaseIOGate
func StorageGO_FixtureReleaseIOGate() {
	fixtureIOGate.Lock()
	defer fixtureIOGate.Unlock()
	if fixtureIOGate.wait != nil {
		close(fixtureIOGate.wait)
		fixtureIOGate.wait = nil
	}
}

//export StorageGO_ABIVersion
func StorageGO_ABIVersion() C.uint32_t { return nativeABIVersion }

//export StorageGO_CreateClient
func StorageGO_CreateClient(configJSON *C.char) *C.char {
	return C.CString(createNativeClientSafely(C.GoString(configJSON)))
}

//export StorageGO_CloseClient
func StorageGO_CloseClient(clientID C.uint64_t) *C.char {
	return C.CString(closeNativeClientSafely(uint64(clientID)))
}

//export StorageGO_Invoke
func StorageGO_Invoke(clientID C.uint64_t, requestJSON *C.char) *C.char {
	return C.CString(`{"success":false,"error":"legacy JSON file transfer is disabled in fixture"}`)
}

//export StorageGO_FreeResult
func StorageGO_FreeResult(result *C.char) {
	if result != nil {
		C.free(unsafe.Pointer(result))
	}
}

//export StorageGO_OpenRead
func StorageGO_OpenRead(clientID C.uint64_t, name *C.char) *C.char {
	return C.CString(nativeControlJSON(func() any {
		return fixtureOpen(uint64(clientID), C.GoString(name), true, false)
	}))
}

//export StorageGO_OpenWrite
func StorageGO_OpenWrite(clientID C.uint64_t, name *C.char, overwrite C.int32_t) *C.char {
	return C.CString(nativeControlJSON(func() any {
		return fixtureOpen(uint64(clientID), C.GoString(name), false, overwrite == 1)
	}))
}

//export StorageGO_ReadChunk
func StorageGO_ReadChunk(id C.uint64_t, buffer unsafe.Pointer, capacity C.uint32_t) *C.char {
	return C.CString(nativeControlJSON(func() any {
		if buffer == nil || capacity == 0 || capacity > maxNativeChunkBytes {
			return nativeChunkResponse{Error: "invalid fixture buffer"}
		}
		return readNativeChunk(uint64(id), unsafe.Slice((*byte)(buffer), int(capacity)))
	}))
}

//export StorageGO_WriteChunk
func StorageGO_WriteChunk(id C.uint64_t, buffer unsafe.Pointer, count C.uint32_t) *C.char {
	return C.CString(nativeControlJSON(func() any {
		if buffer == nil || count == 0 || count > maxNativeChunkBytes {
			return nativeChunkResponse{Error: "invalid fixture buffer"}
		}
		return writeNativeChunk(uint64(id), unsafe.Slice((*byte)(buffer), int(count)))
	}))
}

//export StorageGO_FlushTransfer
func StorageGO_FlushTransfer(id C.uint64_t) *C.char {
	return C.CString(nativeControlJSON(func() any { return flushNativeTransfer(uint64(id)) }))
}

//export StorageGO_CloseTransfer
func StorageGO_CloseTransfer(id C.uint64_t, forceSync C.int32_t) *C.char {
	return C.CString(nativeControlJSON(func() any { return closeNativeTransfer(uint64(id), forceSync == 1) }))
}
