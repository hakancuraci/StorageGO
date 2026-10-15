package main

import "sync"

const maxRetainedNativeBufferBytes = 32 * 1024 * 1024
const maxNativeBuffersPerBucket = 16

var nativeBufferSizes = [...]int{64 * 1024, 256 * 1024, 1024 * 1024, maxNativeChunkBytes}

// Bounds retained scratch memory, not live operations or total process memory.
// Unlike sync.Pool, retention is explicit and independent of GC scheduling.
type nativeBufferPool struct {
	sync.Mutex
	retained int
	buckets  [len(nativeBufferSizes)][][]byte
}

var nativeBuffers nativeBufferPool

func (p *nativeBufferPool) rent(count int) []byte {
	if count < 1 || count > maxNativeChunkBytes {
		return nil
	}
	for index, size := range nativeBufferSizes {
		if count > size {
			continue
		}
		p.Lock()
		bucket := p.buckets[index]
		if len(bucket) != 0 {
			buffer := bucket[len(bucket)-1]
			bucket[len(bucket)-1] = nil
			p.buckets[index] = bucket[:len(bucket)-1]
			p.retained -= size
			p.Unlock()
			return buffer[:count]
		}
		p.Unlock()
		return make([]byte, count, size)
	}
	return nil
}

func (p *nativeBufferPool) giveBack(buffer []byte) {
	for index, size := range nativeBufferSizes {
		if cap(buffer) != size {
			continue
		}
		// Clear the entire backing array, including a previous operation's tail.
		clear(buffer[:size])
		p.Lock()
		if p.retained+size <= maxRetainedNativeBufferBytes && len(p.buckets[index]) < maxNativeBuffersPerBucket {
			p.buckets[index] = append(p.buckets[index], buffer[:size])
			p.retained += size
		}
		p.Unlock()
		return
	}
}
