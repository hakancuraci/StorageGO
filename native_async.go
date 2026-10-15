package main

import (
	"context"
	"encoding/json"
	"sync"
	"sync/atomic"
)

// Admission bounds queued goroutines and buffers across all clients in this
// process. Rejected submissions receive ID 0 and never invoke completion.
const maxNativeAsyncOperations = 1024

type nativeAsyncRequest struct {
	Action     string `json:"action"`
	ClientID   uint64 `json:"clientId"`
	TransferID uint64 `json:"transferId"`
	Path       string `json:"path"`
	Overwrite  bool   `json:"overwrite"`
	ForceSync  bool   `json:"forceSync"`
}

type nativeAsyncOperation struct {
	cancel context.CancelFunc
}

var nativeAsyncOperations = struct {
	sync.Mutex
	items map[uint64]*nativeAsyncOperation
}{items: make(map[uint64]*nativeAsyncOperation)}

var nativeAsyncSlots = make(chan struct{}, maxNativeAsyncOperations)
var nextNativeAsyncID atomic.Uint64

// Test fixtures replace only the opening of a file. All async scheduling,
// cancellation, transfer management and binary buffer paths remain production code.
var nativeAsyncOpenRead = func(ctx context.Context, clientID uint64, path string) nativeTransferResponse {
	return openNativeTransferContext(ctx, clientID, path, true, false)
}
var nativeAsyncOpenWrite = func(ctx context.Context, clientID uint64, path string, overwrite bool) nativeTransferResponse {
	return openNativeTransferContext(ctx, clientID, path, false, overwrite)
}

const nativeAsyncCancelledJSON = `{"success":false,"cancelled":true,"error":"native işlem iptal edildi"}`

// beginNativeAsync does no network I/O on its caller. Input is copied before
// returning; completion is invoked exactly once by a Go goroutine. The completion
// buffers are borrowed and must not be retained after completion returns.
func beginNativeAsync(requestJSON string, writeData []byte, count int, completion func(string, []byte)) uint64 {
	if completion == nil {
		return 0
	}
	select {
	case nativeAsyncSlots <- struct{}{}:
	default:
		return 0
	}

	// Never retain a view of memory supplied by the native host. Bound copies even
	// for malformed input; validation failures are delivered asynchronously.
	var ownedData []byte
	if count > 0 && count <= maxNativeChunkBytes && len(writeData) >= count {
		ownedData = append([]byte(nil), writeData[:count]...)
	}
	ctx, cancel := context.WithCancel(context.Background())
	id := nextNativeAsyncID.Add(1)
	nativeAsyncOperations.Lock()
	nativeAsyncOperations.items[id] = &nativeAsyncOperation{cancel: cancel}
	nativeAsyncOperations.Unlock()

	go func() {
		// Native resources remain owned until I/O has really returned. In
		// particular cancellation never fabricates an early completion for a
		// read/write still using a buffer or file handle.
		var data []byte
		result := nativeControlJSON(func() any {
			var request nativeAsyncRequest
			if err := json.Unmarshal([]byte(requestJSON), &request); err != nil {
				return nativeTransferResponse{Error: "geçersiz async istek JSON"}
			}
			response, bytes := executeNativeAsync(ctx, request, ownedData, count)
			data = bytes
			return response
		})

		// Linearize completion against cancellation before calling managed code.
		// No registry/admission lock is held while running a foreign callback.
		nativeAsyncOperations.Lock()
		delete(nativeAsyncOperations.items, id)
		nativeAsyncOperations.Unlock()
		cancel()
		<-nativeAsyncSlots
		completion(result, data)
	}()
	return id
}

func cancelNativeAsync(id uint64) {
	nativeAsyncOperations.Lock()
	if operation, found := nativeAsyncOperations.items[id]; found {
		operation.cancel()
	}
	nativeAsyncOperations.Unlock()
}

func executeNativeAsync(ctx context.Context, request nativeAsyncRequest, writeData []byte, count int) (any, []byte) {
	// Close operations must release ownership even if already cancelled.
	cleanup := request.Action == "closeTransfer" || request.Action == "closeClient"
	if ctx.Err() != nil && !cleanup {
		return json.RawMessage(nativeAsyncCancelledJSON), nil
	}

	var response any
	var data []byte
	switch request.Action {
	case "openRead":
		response = nativeAsyncOpenRead(ctx, request.ClientID, request.Path)
	case "openWrite":
		response = nativeAsyncOpenWrite(ctx, request.ClientID, request.Path, request.Overwrite)
	case "read":
		if count < 1 || count > maxNativeChunkBytes {
			return nativeChunkResponse{Error: "geçersiz async chunk boyutu (1..4 MiB)"}, nil
		}
		buffer := make([]byte, count)
		chunk := readNativeChunkAsync(ctx, request.TransferID, buffer)
		response = chunk
		if chunk.Success {
			data = buffer[:chunk.BytesTransferred]
		}
	case "write":
		if count < 1 || count > maxNativeChunkBytes || len(writeData) != count {
			return nativeChunkResponse{Error: "geçersiz async write buffer veya boyutu"}, nil
		}
		response = writeNativeChunkAsync(ctx, request.TransferID, writeData)
	case "flush":
		response = flushNativeTransferAsync(ctx, request.TransferID)
	case "closeTransfer":
		response = closeNativeTransfer(request.TransferID, request.ForceSync)
	case "closeClient":
		response = closeNativeClient(request.ClientID)
	default:
		return nativeTransferResponse{Error: "desteklenmeyen async işlem"}, nil
	}

	if ctx.Err() != nil {
		// An open may have succeeded before cancellation was observed. Close its
		// handle before returning cancellation so the host never loses ownership.
		if opened, ok := response.(nativeTransferResponse); ok && opened.Success && opened.TransferID != 0 {
			_ = closeNativeTransfer(opened.TransferID, false)
		}
		return json.RawMessage(nativeAsyncCancelledJSON), nil
	}
	return response, data
}
