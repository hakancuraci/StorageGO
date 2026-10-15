//go:build cgo

package main

/*
#include <stdlib.h>
#include <stdint.h>

// All completion pointers are borrowed, valid only until this callback returns.
// The callback must not free them and must not propagate exceptions to C/Go.
typedef void (*StorageGO_Completion)(uint64_t token, const char* result,
                                    const void* data, uint32_t count);

static inline void storagego_complete(StorageGO_Completion completion,
    uint64_t token, const char* result, const void* data, uint32_t count) {
    completion(token, result, data, count);
}
*/
import "C"

import (
	"runtime"
	"unsafe"
)

//export StorageGO_BeginAsync
func StorageGO_BeginAsync(requestJSON *C.char, buffer unsafe.Pointer, count C.uint32_t, completion C.StorageGO_Completion, token C.uint64_t) C.uint64_t {
	if completion == nil {
		return 0
	}
	request := ""
	if requestJSON != nil {
		request = C.GoString(requestJSON)
	}
	var writeData []byte
	if buffer != nil && count > 0 && count <= maxNativeChunkBytes {
		// beginNativeAsync copies this borrowed buffer before returning to .NET.
		writeData = unsafe.Slice((*byte)(buffer), int(count))
	}
	id := beginNativeAsync(request, writeData, int(count), func(result string, data []byte) {
		encoded := C.CString(result)
		defer C.free(unsafe.Pointer(encoded))
		var pointer unsafe.Pointer
		if len(data) != 0 {
			pointer = unsafe.Pointer(&data[0])
		}
		// A pointer-free Go byte buffer may be borrowed by C for this call only.
		// Managed completion copies its contents before returning to Go.
		C.storagego_complete(completion, token, encoded, pointer, C.uint32_t(len(data)))
		runtime.KeepAlive(data)
	})
	return C.uint64_t(id)
}

//export StorageGO_CancelAsync
func StorageGO_CancelAsync(operationID C.uint64_t) {
	cancelNativeAsync(uint64(operationID))
}

//export StorageGO_ReleaseClient
func StorageGO_ReleaseClient(clientID C.uint64_t) {
	// A finalizer/rejection fallback must neither wait for I/O on a managed
	// thread nor compete for admission with ordinary data operations.
	go closeNativeClientSafely(uint64(clientID))
}

//export StorageGO_ReleaseTransfer
func StorageGO_ReleaseTransfer(transferID C.uint64_t) {
	go func() {
		_ = nativeControlJSON(func() any { return closeNativeTransfer(uint64(transferID), false) })
	}()
}
