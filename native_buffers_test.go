package main

import (
	"bytes"
	"errors"
	"fmt"
	"os"
	"runtime"
	"sync"
	"testing"
	"time"
)

func TestNativeBufferPoolSizesClearingAndReuse(t *testing.T) {
	var pool nativeBufferPool
	for _, count := range []int{1, 65536, 65537, 262145, maxNativeChunkBytes} {
		buffer := pool.rent(count)
		if len(buffer) != count || cap(buffer) < count {
			t.Fatalf("rent(%d) = len %d cap %d", count, len(buffer), cap(buffer))
		}
		backing := buffer[:cap(buffer)]
		for index := range backing {
			backing[index] = 0xa5
		}
		pool.giveBack(buffer)
		next := pool.rent(count)
		if &next[0] != &buffer[0] {
			t.Fatal("retained buffer was not reused")
		}
		if !bytes.Equal(next[:cap(next)], make([]byte, cap(next))) {
			t.Fatal("old buffer contents escaped")
		}
		pool.giveBack(next)
	}
	if pool.rent(0) != nil || pool.rent(-1) != nil || pool.rent(maxNativeChunkBytes+1) != nil {
		t.Fatal("invalid size accepted")
	}
	retained := pool.retained
	pool.giveBack(make([]byte, 123))
	if pool.retained != retained {
		t.Fatal("foreign capacity retained")
	}
}

func TestNativeBufferPoolRetentionIsBounded(t *testing.T) {
	var pool nativeBufferPool
	for _, size := range nativeBufferSizes {
		for index := 0; index < maxNativeBuffersPerBucket+4; index++ {
			pool.giveBack(make([]byte, size))
		}
	}
	if pool.retained > maxRetainedNativeBufferBytes {
		t.Fatalf("retained %d bytes", pool.retained)
	}
	var total int
	for index, bucket := range pool.buckets {
		if len(bucket) > maxNativeBuffersPerBucket {
			t.Fatal("bucket limit exceeded")
		}
		total += len(bucket) * nativeBufferSizes[index]
	}
	if total != pool.retained {
		t.Fatalf("retained accounting = %d, actual = %d", pool.retained, total)
	}
}

func TestNativeBufferPoolConcurrentOwnership(t *testing.T) {
	var pool nativeBufferPool
	var workers sync.WaitGroup
	for worker := 0; worker < 16; worker++ {
		workers.Add(1)
		go func(value byte) {
			defer workers.Done()
			for iteration := 0; iteration < 40; iteration++ {
				buffer := pool.rent(65537)
				for index := range buffer {
					if buffer[index] != 0 {
						t.Error("buffer reused before its previous owner returned it")
						break
					}
					buffer[index] = value
				}
				pool.giveBack(buffer)
			}
		}(byte(worker + 1))
	}
	workers.Wait()
}

func TestNativeAsyncReadBufferRemainsOwnedUntilCallbackReturns(t *testing.T) {
	id := addMemoryTransfer(newMemoryTransferClient(t), &memoryTransferFile{reader: bytes.NewReader([]byte{1, 2, 3})}, true)
	borrowed := make(chan []byte, 1)
	release := make(chan struct{})
	finished := make(chan bool, 1)
	var once sync.Once
	defer once.Do(func() { close(release) })
	operation := beginNativeAsync(fmt.Sprintf(`{"action":"read","transferId":%d}`, id), nil, 64, func(_ string, data []byte) {
		borrowed <- data
		<-release
		finished <- bytes.Equal(data, []byte{1, 2, 3})
	})
	if operation == 0 {
		t.Fatal("read rejected")
	}
	var data []byte
	select {
	case data = <-borrowed:
	case <-time.After(5 * time.Second):
		t.Fatal("callback did not start")
	}
	for index := 0; index < 100; index++ {
		buffer := nativeBuffers.rent(64)
		if &buffer[0] == &data[0] {
			t.Fatal("callback buffer was returned early")
		}
		buffer[0] = 99
		nativeBuffers.giveBack(buffer)
	}
	once.Do(func() { close(release) })
	select {
	case valid := <-finished:
		if !valid {
			t.Fatal("borrowed callback data was overwritten")
		}
	case <-time.After(5 * time.Second):
		t.Fatal("callback did not finish")
	}
}

func TestReusableNativeFileErrorClassification(t *testing.T) {
	for _, err := range []error{os.ErrNotExist, os.ErrExist, os.ErrPermission, &os.PathError{Op: "open", Path: "safe", Err: os.ErrNotExist}} {
		if !reusableNativeFileError(err) {
			t.Fatalf("normal file error invalidates client: %v", err)
		}
	}
	if reusableNativeFileError(errors.New("transport failed")) || reusableNativeFileError(nil) {
		t.Fatal("unknown error classified as reusable")
	}
}

func TestNativeBufferPoolWarmReuseDoesNotAllocate(t *testing.T) {
	var pool nativeBufferPool
	pool.giveBack(pool.rent(1024 * 1024))
	allocations := testing.AllocsPerRun(20, func() {
		buffer := pool.rent(1024 * 1024)
		buffer[0] = 42
		pool.giveBack(buffer)
	})
	if allocations != 0 {
		t.Fatalf("warm scratch buffer allocates %v objects per rent/return", allocations)
	}
}

// A scratch allocation microbenchmark, not an SMB throughput benchmark.
func BenchmarkNativeBufferAllocation(b *testing.B) {
	const size = 1024 * 1024
	b.Run("allocate", func(b *testing.B) {
		b.ReportAllocs()
		b.SetBytes(size)
		for index := 0; index < b.N; index++ {
			buffer := make([]byte, size)
			buffer[0] = 42
			runtime.KeepAlive(buffer)
		}
	})
	b.Run("pooled-cleared", func(b *testing.B) {
		var pool nativeBufferPool
		pool.giveBack(pool.rent(size))
		b.ReportAllocs()
		b.SetBytes(size)
		b.ResetTimer()
		for index := 0; index < b.N; index++ {
			buffer := pool.rent(size)
			buffer[0] = 42
			pool.giveBack(buffer)
		}
	})
}
