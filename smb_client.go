package main

import (
	"bytes"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/hirochachacha/go-smb2"
)

const (
	defaultSMBPort       = 445
	defaultDialTimeout   = 15 * time.Second
	defaultMaxReadBytes  = int64(256 * 1024 * 1024)
	fileTransferBufBytes = 1024 * 1024
)

// NativeClientConfig contains connection and authentication settings for one
// isolated SMB client. Password is never returned by the native API.
type NativeClientConfig struct {
	Server             string `json:"server"`
	ShareName          string `json:"shareName"`
	Username           string `json:"username"`
	Password           string `json:"password"`
	Domain             string `json:"domain,omitempty"`
	Port               int    `json:"port,omitempty"`
	DialTimeoutSeconds int    `json:"dialTimeoutSeconds,omitempty"`
	MaxReadBytes       int64  `json:"maxReadBytes,omitempty"`
}

func (c *NativeClientConfig) setDefaultsAndValidate() error {
	c.Server = strings.TrimSpace(c.Server)
	c.ShareName = strings.Trim(strings.TrimSpace(c.ShareName), `\ /`)
	if c.Server == "" {
		return errors.New("server alanı gereklidir")
	}
	if strings.Contains(c.Server, "://") || strings.ContainsAny(c.Server, `\ /`) {
		return errors.New("server yalnızca sunucu adı veya IP adresi olmalıdır")
	}
	if c.ShareName == "" {
		return errors.New("shareName alanı gereklidir")
	}
	if c.Username == "" {
		return errors.New("username alanı gereklidir")
	}
	if c.Port == 0 {
		c.Port = defaultSMBPort
	}
	if c.Port < 1 || c.Port > 65535 {
		return errors.New("port 1 ile 65535 arasında olmalıdır")
	}
	if c.DialTimeoutSeconds == 0 {
		c.DialTimeoutSeconds = int(defaultDialTimeout / time.Second)
	}
	if c.DialTimeoutSeconds < 1 || c.DialTimeoutSeconds > 300 {
		return errors.New("dialTimeoutSeconds 1 ile 300 arasında olmalıdır")
	}
	if c.MaxReadBytes == 0 {
		c.MaxReadBytes = defaultMaxReadBytes
	}
	if c.MaxReadBytes < 1 {
		return errors.New("maxReadBytes pozitif olmalıdır")
	}
	return nil
}

// nativeSMBClient owns a TCP connection, SMB session and mounted share. opMu
// makes Close and I/O race-free and keeps each SMB session isolated.
type nativeSMBClient struct {
	config  NativeClientConfig
	opMu    sync.Mutex
	closed  bool
	conn    net.Conn
	session *smb2.Session
	share   *smb2.Share
}

func newNativeSMBClient(config NativeClientConfig) (*nativeSMBClient, error) {
	if err := config.setDefaultsAndValidate(); err != nil {
		return nil, err
	}
	return &nativeSMBClient{config: config}, nil
}

func (c *nativeSMBClient) endpoint() string {
	return net.JoinHostPort(c.config.Server, strconv.Itoa(c.config.Port))
}

func (c *nativeSMBClient) connectLocked() (*smb2.Share, error) {
	if c.closed {
		return nil, errors.New("client kapatıldı")
	}
	if c.share != nil {
		return c.share, nil
	}

	conn, err := net.DialTimeout("tcp", c.endpoint(), time.Duration(c.config.DialTimeoutSeconds)*time.Second)
	if err != nil {
		return nil, fmt.Errorf("SMB sunucusuna bağlanılamadı: %w", err)
	}
	dialer := &smb2.Dialer{Initiator: &smb2.NTLMInitiator{
		User: c.config.Username, Password: c.config.Password, Domain: c.config.Domain,
	}}
	session, err := dialer.Dial(conn)
	if err != nil {
		_ = conn.Close()
		return nil, fmt.Errorf("SMB kimlik doğrulaması başarısız: %w", err)
	}
	share, err := session.Mount(c.config.ShareName)
	if err != nil {
		_ = session.Logoff()
		_ = conn.Close()
		return nil, fmt.Errorf("SMB paylaşımına bağlanılamadı: %w", err)
	}

	c.conn, c.session, c.share = conn, session, share
	return share, nil
}

func (c *nativeSMBClient) resetLocked() {
	if c.share != nil {
		_ = c.share.Umount()
		c.share = nil
	}
	if c.session != nil {
		_ = c.session.Logoff()
		c.session = nil
	}
	if c.conn != nil {
		_ = c.conn.Close()
		c.conn = nil
	}
}

func (c *nativeSMBClient) close() {
	c.opMu.Lock()
	defer c.opMu.Unlock()
	c.closed = true
	c.resetLocked()
	c.config.Password = ""
}

func (c *nativeSMBClient) read(remotePath string) FileResponse {
	c.opMu.Lock()
	defer c.opMu.Unlock()

	if strings.TrimSpace(remotePath) == "" {
		return failedFileResponse("path alanı gereklidir")
	}
	share, err := c.connectLocked()
	if err != nil {
		return failedFileResponse(err.Error())
	}
	file, err := share.Open(remotePath)
	if err != nil {
		c.resetLocked()
		return failedFileResponse(fmt.Sprintf("dosya açılamadı: %v", err))
	}
	defer file.Close()

	info, err := file.Stat()
	if err != nil {
		return failedFileResponse(fmt.Sprintf("dosya bilgisi alınamadı: %v", err))
	}
	if info.IsDir() {
		return failedFileResponse("path bir dosya olmalıdır")
	}
	if info.Size() > c.config.MaxReadBytes {
		return failedFileResponse(fmt.Sprintf("dosya boyutu maxReadBytes sınırını aşıyor (%d > %d)", info.Size(), c.config.MaxReadBytes))
	}

	data, err := io.ReadAll(io.LimitReader(file, c.config.MaxReadBytes+1))
	if err != nil {
		c.resetLocked()
		return failedFileResponse(fmt.Sprintf("dosya okunamadı: %v", err))
	}
	if int64(len(data)) > c.config.MaxReadBytes {
		return failedFileResponse("dosya okuma sırasında maxReadBytes sınırını aştı")
	}
	return FileResponse{
		Success: true, Message: "Dosya başarıyla okundu", FileName: filepath.Base(remotePath),
		FileSize: int64(len(data)), MimeType: getMimeType(remotePath, data),
		Content: base64.StdEncoding.EncodeToString(data),
	}
}

func (c *nativeSMBClient) write(remotePath string, content []byte, overwrite, forceSync bool, mimeType string) FileResponse {
	c.opMu.Lock()
	defer c.opMu.Unlock()

	if strings.TrimSpace(remotePath) == "" {
		return failedFileResponse("path alanı gereklidir")
	}
	share, err := c.connectLocked()
	if err != nil {
		return failedFileResponse(err.Error())
	}
	flags := os.O_CREATE | os.O_WRONLY
	if overwrite {
		flags |= os.O_TRUNC
	} else {
		flags |= os.O_EXCL
	}
	file, err := share.OpenFile(remotePath, flags, 0o644)
	if err != nil {
		c.resetLocked()
		return failedFileResponse(fmt.Sprintf("dosya açılamadı: %v", err))
	}

	written, writeErr := io.CopyBuffer(file, bytes.NewReader(content), make([]byte, fileTransferBufBytes))
	if writeErr == nil && forceSync {
		writeErr = file.Sync()
	}
	closeErr := file.Close()
	if writeErr == nil {
		writeErr = closeErr
	}
	if writeErr != nil {
		c.resetLocked()
		return failedFileResponse(fmt.Sprintf("dosya yazılamadı: %v", writeErr))
	}
	if mimeType == "" {
		mimeType = getMimeType(remotePath, content)
	}
	return FileResponse{
		Success: true, Message: "Dosya başarıyla yazıldı", FileName: filepath.Base(remotePath),
		FileSize: written, MimeType: mimeType,
	}
}

func failedFileResponse(message string) FileResponse {
	return FileResponse{Success: false, Error: message}
}
