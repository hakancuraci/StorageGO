//go:build cgo

package main

/*
#include <stdlib.h>
#include <stdint.h>
*/
import "C"

import "unsafe"

//export StorageGO_ABIVersion
func StorageGO_ABIVersion() C.uint32_t {
	return nativeABIVersion
}

//export StorageGO_OpenRead
func StorageGO_OpenRead(clientID C.uint64_t, remotePath *C.char) *C.char {
	return C.CString(nativeControlJSON(func() any {
		if remotePath == nil {
			return nativeTransferResponse{Error: "path NULL olamaz"}
		}
		return openNativeRead(uint64(clientID), C.GoString(remotePath))
	}))
}

//export StorageGO_OpenWrite
func StorageGO_OpenWrite(clientID C.uint64_t, remotePath *C.char, overwrite C.int32_t) *C.char {
	return C.CString(nativeControlJSON(func() any {
		if remotePath == nil {
			return nativeTransferResponse{Error: "path NULL olamaz"}
		}
		if overwrite != 0 && overwrite != 1 {
			return nativeTransferResponse{Error: "overwrite 0 veya 1 olmalıdır"}
		}
		return openNativeWrite(uint64(clientID), C.GoString(remotePath), overwrite == 1)
	}))
}

//export StorageGO_ReadChunk
func StorageGO_ReadChunk(transferID C.uint64_t, buffer unsafe.Pointer, capacity C.uint32_t) *C.char {
	return C.CString(nativeControlJSON(func() any {
		if buffer == nil || capacity == 0 || capacity > maxNativeChunkBytes {
			return nativeChunkResponse{Error: "geçersiz chunk buffer veya boyutu (1..4 MiB)"}
		}
		// This view is valid only during the call; neither registry nor SMB client
		// retains the caller's memory after returning to the native host.
		return readNativeChunk(uint64(transferID), unsafe.Slice((*byte)(buffer), int(capacity)))
	}))
}

//export StorageGO_WriteChunk
func StorageGO_WriteChunk(transferID C.uint64_t, buffer unsafe.Pointer, count C.uint32_t) *C.char {
	return C.CString(nativeControlJSON(func() any {
		if buffer == nil || count == 0 || count > maxNativeChunkBytes {
			return nativeChunkResponse{Error: "geçersiz chunk buffer veya boyutu (1..4 MiB)"}
		}
		return writeNativeChunk(uint64(transferID), unsafe.Slice((*byte)(buffer), int(count)))
	}))
}

//export StorageGO_FlushTransfer
func StorageGO_FlushTransfer(transferID C.uint64_t) *C.char {
	return C.CString(nativeControlJSON(func() any {
		return flushNativeTransfer(uint64(transferID))
	}))
}

//export StorageGO_CloseTransfer
func StorageGO_CloseTransfer(transferID C.uint64_t, forceSync C.int32_t) *C.char {
	return C.CString(nativeControlJSON(func() any {
		if forceSync != 0 && forceSync != 1 {
			return nativeTransferResponse{Error: "forceSync 0 veya 1 olmalıdır"}
		}
		return closeNativeTransfer(uint64(transferID), forceSync == 1)
	}))
}

//export StorageGO_CreateClient
func StorageGO_CreateClient(configJSON *C.char) *C.char {
	if configJSON == nil {
		return C.CString(marshalCreateResponse(nativeCreateResponse{Success: false, Error: "configJSON NULL olamaz"}))
	}
	return C.CString(createNativeClientSafely(C.GoString(configJSON)))
}

//export StorageGO_Invoke
func StorageGO_Invoke(clientID C.uint64_t, requestJSON *C.char) *C.char {
	if requestJSON == nil {
		return C.CString(marshalNativeResponse(failedFileResponse("requestJSON NULL olamaz")))
	}
	return C.CString(invokeNativeRequestSafely(uint64(clientID), C.GoString(requestJSON)))
}

//export StorageGO_CloseClient
func StorageGO_CloseClient(clientID C.uint64_t) *C.char {
	return C.CString(closeNativeClientSafely(uint64(clientID)))
}

//export StorageGO_FreeResult
func StorageGO_FreeResult(result *C.char) {
	if result != nil {
		C.free(unsafe.Pointer(result))
	}
}
