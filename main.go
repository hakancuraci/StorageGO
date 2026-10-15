package main

import (
	"bufio"
	"bytes"
	"encoding/base64"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/gofiber/fiber/v2"
	"github.com/gofiber/fiber/v2/middleware/recover"
	"github.com/hirochachacha/go-smb2"
)

// Config yapısı
type SMBConfig struct {
	Server    string `json:"server"`
	ShareName string `json:"shareName"`
	Username  string `json:"username"`
	Password  string `json:"password"`
	Domain    string `json:"domain"`
}

// Response yapısı
type FileResponse struct {
	Success  bool   `json:"success"`
	Message  string `json:"message,omitempty"`
	FileName string `json:"fileName,omitempty"`
	FileSize int64  `json:"fileSize,omitempty"`
	MimeType string `json:"mimeType,omitempty"`
	Content  string `json:"content,omitempty"` // Base64 encoded
	Error    string `json:"error,omitempty"`
}

// Global SMB config
var smbConfig = SMBConfig{
	Server:    "",
	ShareName: "",
	Username:  "",
	Password:  "",
	Domain:    "",
}

type smbPool struct {
	mu      sync.Mutex
	conn    net.Conn
	session *smb2.Session
	share   *smb2.Share
}

func main() {
	app := fiber.New()

	app.Use(recover.New())
	app.Use(requestDurationLogger())

	app.Get("/health", healthCheck)
	app.Get("/file", getFile)
	app.Post("/file", getFilePost)
	app.Get("/file/download", downloadFile)
	app.Post("/file/write", uploadFile)

	fmt.Println("SMB File Service başlatılıyor...")
	fmt.Println("http://localhost:8080")

	if err := app.Listen(":8080"); err != nil {
		fmt.Printf("Sunucu başlatılamadı: %v\n", err)
	}
}

// requestDurationLogger logs per-request duration for observability.
func requestDurationLogger() fiber.Handler {
	return func(c *fiber.Ctx) error {
		start := time.Now()
		err := c.Next()

		duration := time.Since(start)
		path := c.Route().Path
		if path == "" {
			path = c.Path()
		}

		fmt.Printf("[SERVICE-DURATION] %s %s -> %d in %v\n",
			c.Method(),
			path,
			c.Response().StatusCode(),
			duration,
		)

		return err
	}
}

var globalSMBPool smbPool

func (p *smbPool) getShare() (*smb2.Share, error) {
	p.mu.Lock()
	defer p.mu.Unlock()

	if p.share != nil {
		return p.share, nil
	}

	conn, err := net.Dial("tcp", smbConfig.Server+":445")
	if err != nil {
		return nil, fmt.Errorf("TCP bağlantısı kurulamadı: %w", err)
	}

	dialer := &smb2.Dialer{
		Initiator: &smb2.NTLMInitiator{
			User:     smbConfig.Username,
			Password: smbConfig.Password,
			Domain:   smbConfig.Domain,
		},
	}

	session, err := dialer.Dial(conn)
	if err != nil {
		_ = conn.Close()
		return nil, fmt.Errorf("SMB oturumu kurulamadı: %w", err)
	}

	share, err := session.Mount(smbConfig.ShareName)
	if err != nil {
		_ = session.Logoff()
		_ = conn.Close()
		return nil, fmt.Errorf("Share'e bağlanılamadı: %w", err)
	}

	p.conn = conn
	p.session = session
	p.share = share

	return share, nil
}

func (p *smbPool) reset() {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.resetLocked()
}

func (p *smbPool) resetLocked() {
	if p.share != nil {
		_ = p.share.Umount()
		p.share = nil
	}
	if p.session != nil {
		_ = p.session.Logoff()
		p.session = nil
	}
	if p.conn != nil {
		_ = p.conn.Close()
		p.conn = nil
	}
}

// Health check endpoint
func healthCheck(c *fiber.Ctx) error {
	return c.Status(http.StatusOK).JSON(fiber.Map{
		"status":    "OK",
		"service":   "SMB File Service",
		"timestamp": "2025-08-20 06:55:47",
		"user":      "",
	})
}

// GET /file?path=TEST-OLD\dss debug\example.txt

func getFile(c *fiber.Ctx) error {
	path := c.Query("path")
	if path == "" {
		return c.Status(http.StatusBadRequest).JSON(FileResponse{
			Success: false,
			Error:   "path parametresi gerekli",
		})
	}

	response := readFileFromSMB(path)

	if response.Success {
		return c.Status(http.StatusOK).JSON(response)
	} else {
		return c.Status(http.StatusInternalServerError).JSON(response)
	}
}

func getFilePost(c *fiber.Ctx) error {
	var request struct {
		Path string `json:"path"`
	}

	if err := c.BodyParser(&request); err != nil {
		return c.Status(http.StatusBadRequest).JSON(FileResponse{
			Success: false,
			Error:   "Geçersiz JSON formatı: " + err.Error(),
		})
	}

	response := readFileFromSMB(request.Path)

	if response.Success {
		return c.Status(http.StatusOK).JSON(response)
	} else {
		return c.Status(http.StatusInternalServerError).JSON(response)
	}
}

func downloadFile(c *fiber.Ctx) error {
	path := c.Query("path")
	if path == "" {
		return c.Status(http.StatusBadRequest).JSON(FileResponse{
			Success: false,
			Error:   "path parametresi gerekli",
		})
	}

	share, err := globalSMBPool.getShare()
	if err != nil {
		return c.Status(http.StatusInternalServerError).JSON(FileResponse{
			Success: false,
			Error:   fmt.Sprintf("SMB oturumu hazır değil: %v", err),
		})
	}

	file, err := share.Open(path)
	if err != nil {
		globalSMBPool.reset()
		share, retryErr := globalSMBPool.getShare()
		if retryErr != nil {
			return c.Status(http.StatusInternalServerError).JSON(FileResponse{
				Success: false,
				Error:   fmt.Sprintf("SMB oturumu hazır değil: %v", retryErr),
			})
		}

		file, err = share.Open(path)
		if err != nil {
			return c.Status(http.StatusInternalServerError).JSON(FileResponse{
				Success: false,
				Error:   fmt.Sprintf("Dosya açılamadı: %v", err),
			})
		}
	}
	closeFile := true
	defer func() {
		if closeFile {
			_ = file.Close()
		}
	}()

	stat, err := file.Stat()
	if err != nil {
		globalSMBPool.reset()
		return c.Status(http.StatusInternalServerError).JSON(FileResponse{
			Success: false,
			Error:   fmt.Sprintf("Dosya bilgisi alınamadı: %v", err),
		})
	}

	if stat.IsDir() {
		return c.Status(http.StatusBadRequest).JSON(FileResponse{
			Success: false,
			Error:   "Dizin indirilemez",
		})
	}

	mimeType := getMimeType(path, nil)
	if mimeType == "application/octet-stream" && stat.Size() > 0 {
		sniff := make([]byte, 512)
		n, readErr := file.Read(sniff)
		if readErr != nil && readErr != io.EOF {
			globalSMBPool.reset()
			return c.Status(http.StatusInternalServerError).JSON(FileResponse{
				Success: false,
				Error:   fmt.Sprintf("Dosya okunamadı: %v", readErr),
			})
		}
		sniff = sniff[:n]
		if n > 0 {
			mimeType = getMimeType(path, sniff)
		}
		if _, seekErr := file.Seek(0, io.SeekStart); seekErr != nil {
			globalSMBPool.reset()
			return c.Status(http.StatusInternalServerError).JSON(FileResponse{
				Success: false,
				Error:   fmt.Sprintf("Dosya konumu sıfırlanamadı: %v", seekErr),
			})
		}
	}

	c.Set("Content-Disposition", fmt.Sprintf("attachment; filename=%q", filepath.Base(path)))
	c.Type(mimeType)
	c.Set("Content-Length", strconv.FormatInt(stat.Size(), 10))
	c.Status(http.StatusOK)

	closeFile = false
	c.Context().SetBodyStreamWriter(func(w *bufio.Writer) {
		defer file.Close()
		buffer := make([]byte, 1<<20)
		for {
			n, readErr := file.Read(buffer)
			if n > 0 {
				if _, writeErr := w.Write(buffer[:n]); writeErr != nil {
					return
				}
			}
			if readErr != nil {
				if readErr != io.EOF {
					fmt.Printf("[DOWNLOAD] SMB okuma hatası: %v\n", readErr)
				}
				_ = w.Flush()
				return
			}
		}
	})

	return nil
}

func uploadFile(c *fiber.Ctx) error {
	path := c.FormValue("path")
	if path == "" {
		return c.Status(http.StatusBadRequest).JSON(FileResponse{
			Success: false,
			Error:   "path parametresi gerekli",
		})
	}

	overwriteValue := c.FormValue("overwrite")
	overwrite := false
	if overwriteValue != "" {
		parsed, err := strconv.ParseBool(overwriteValue)
		if err != nil {
			return c.Status(http.StatusBadRequest).JSON(FileResponse{
				Success: false,
				Error:   "overwrite parametresi geçersiz",
			})
		}
		overwrite = parsed
	}

	syncValue := c.FormValue("sync")
	forceSync := false
	if syncValue != "" {
		parsed, err := strconv.ParseBool(syncValue)
		if err != nil {
			return c.Status(http.StatusBadRequest).JSON(FileResponse{
				Success: false,
				Error:   "sync parametresi geçersiz",
			})
		}
		forceSync = parsed
	}

	fileHeader, err := c.FormFile("file")
	if err != nil {
		return c.Status(http.StatusBadRequest).JSON(FileResponse{
			Success: false,
			Error:   "file parametresi gerekli",
		})
	}

	src, err := fileHeader.Open()
	if err != nil {
		return c.Status(http.StatusBadRequest).JSON(FileResponse{
			Success: false,
			Error:   "Yüklenen dosya açılamadı: " + err.Error(),
		})
	}
	defer src.Close()

	sniff := make([]byte, 512)
	n, err := src.Read(sniff)
	if err != nil && err != io.EOF {
		return c.Status(http.StatusBadRequest).JSON(FileResponse{
			Success: false,
			Error:   "Dosya içeriği okunamadı: " + err.Error(),
		})
	}
	sniff = sniff[:n]

	reader := io.MultiReader(bytes.NewReader(sniff), src)
	mimeType := getMimeType(fileHeader.Filename, sniff)

	response := writeFileToSMB(path, reader, overwrite, mimeType, forceSync)
	if response.Success {
		if response.FileSize == 0 && fileHeader.Size > 0 {
			response.FileSize = fileHeader.Size
		}
	}

	if response.Success {
		return c.Status(http.StatusOK).JSON(response)
	} else {
		return c.Status(http.StatusInternalServerError).JSON(response)
	}
}

func writeFileToSMB(filePath string, reader io.Reader, overwrite bool, mimeType string, forceSync bool) FileResponse {
	if filePath == "" {
		return FileResponse{
			Success: false,
			Error:   "Dosya yolu boş olamaz",
		}
	}

	share, err := globalSMBPool.getShare()
	if err != nil {
		return FileResponse{
			Success: false,
			Error:   fmt.Sprintf("SMB oturumu hazır değil: %v", err),
		}
	}

	flags := os.O_CREATE | os.O_WRONLY
	if overwrite {
		flags |= os.O_TRUNC
	} else {
		flags |= os.O_EXCL
	}

	file, err := share.OpenFile(filePath, flags, 0o644)
	if err != nil {
		globalSMBPool.reset()
		share, retryErr := globalSMBPool.getShare()
		if retryErr != nil {
			return FileResponse{
				Success: false,
				Error:   fmt.Sprintf("SMB oturumu hazır değil: %v", retryErr),
			}
		}

		file, err = share.OpenFile(filePath, flags, 0o644)
		if err != nil {
			return FileResponse{
				Success: false,
				Error:   fmt.Sprintf("Dosya açılamadı: %v", err),
			}
		}
	}
	defer file.Close()

	written, err := writeFileInChunks(file, reader)
	if err != nil {
		globalSMBPool.reset()
		return FileResponse{
			Success: false,
			Error:   fmt.Sprintf("Dosya yazılamadı: %v", err),
		}
	}

	if forceSync {
		if syncErr := file.Sync(); syncErr != nil {
			globalSMBPool.reset()
			return FileResponse{
				Success: false,
				Error:   fmt.Sprintf("Dosya senkronize edilemedi: %v", syncErr),
			}
		}
	}

	if mimeType == "" {
		mimeType = getMimeType(filePath, nil)
	}

	return FileResponse{
		Success:  true,
		Message:  "Dosya başarıyla yazıldı",
		FileName: filepath.Base(filePath),
		FileSize: written,
		MimeType: mimeType,
	}
}

func writeFileInChunks(file *smb2.File, reader io.Reader) (int64, error) {
	buffer := make([]byte, 1<<20) // 1MB chunks to reduce SMB round trips
	written, err := io.CopyBuffer(file, reader, buffer)
	if err != nil {
		return written, fmt.Errorf("yazma hatası: %w", err)
	}

	return written, nil
}

func readFileFromSMB(filePath string) FileResponse {
	share, err := globalSMBPool.getShare()
	if err != nil {
		return FileResponse{
			Success: false,
			Error:   fmt.Sprintf("SMB oturumu hazır değil: %v", err),
		}
	}

	file, err := share.Open(filePath)
	if err != nil {
		globalSMBPool.reset()
		share, retryErr := globalSMBPool.getShare()
		if retryErr != nil {
			return FileResponse{
				Success: false,
				Error:   fmt.Sprintf("SMB oturumu hazır değil: %v", retryErr),
			}
		}

		file, err = share.Open(filePath)
		if err != nil {
			return FileResponse{
				Success: false,
				Error:   fmt.Sprintf("Dosya açılamadı: %v", err),
			}
		}
	}
	defer file.Close()

	stat, err := file.Stat()
	if err != nil {
		globalSMBPool.reset()
		return FileResponse{
			Success: false,
			Error:   fmt.Sprintf("Dosya bilgisi alınamadı: %v", err),
		}
	}

	data, err := readFileInChunks(file, stat.Size())
	if err != nil {
		globalSMBPool.reset()
		return FileResponse{
			Success: false,
			Error:   fmt.Sprintf("Dosya okunamadı: %v", err),
		}
	}

	base64Content := base64.StdEncoding.EncodeToString(data)
	mimeType := getMimeType(filePath, data)

	return FileResponse{
		Success:  true,
		Message:  "Dosya başarıyla okundu",
		FileName: filepath.Base(filePath),
		FileSize: stat.Size(),
		MimeType: mimeType,
		Content:  base64Content,
	}
}

func readFileInChunks(file *smb2.File, fileSize int64) ([]byte, error) {
	if fileSize == 0 {
		return []byte{}, nil
	}

	fmt.Printf("Dosya boyutu: %d bytes - okuma başlıyor...\n", fileSize)

	chunkSize := 1 << 20 // 1MB chunks
	if fileSize < int64(chunkSize) {
		chunkSize = int(fileSize)
	}
	if chunkSize == 0 {
		chunkSize = 1 << 20
	}

	result := make([]byte, 0, fileSize)
	buffer := make([]byte, chunkSize)

	totalRead := int64(0)
	startTime := time.Now()

	for {
		n, err := file.Read(buffer)
		if err != nil {
			if err == io.EOF {
				if n > 0 {
					result = append(result, buffer[:n]...)
					totalRead += int64(n)
				}
				break
			}
			return nil, fmt.Errorf("okuma hatası: %v", err)
		}

		result = append(result, buffer[:n]...)
		totalRead += int64(n)

		if totalRead%int64(1024*1024) == 0 {
			elapsed := time.Since(startTime)
			fmt.Printf("Okundu: %d/%d bytes (%.1f%%) - %v\n",
				totalRead, fileSize,
				float64(totalRead)/float64(fileSize)*100,
				elapsed)
		}
	}

	elapsed := time.Since(startTime)
	fmt.Printf("Okuma tamamlandı: %d bytes - %v\n", totalRead, elapsed)

	return result, nil
}

// MIME type belirleme
func getMimeType(filename string, data []byte) string {
	ext := strings.ToLower(filepath.Ext(filename))

	// Dosya uzantısına göre
	switch ext {
	case ".pdf":
		return "application/pdf"
	case ".txt":
		return "text/plain"
	case ".json":
		return "application/json"
	case ".xml":
		return "application/xml"
	case ".jpg", ".jpeg":
		return "image/jpeg"
	case ".png":
		return "image/png"
	case ".gif":
		return "image/gif"
	case ".doc":
		return "application/msword"
	case ".docx":
		return "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
	case ".xls":
		return "application/vnd.ms-excel"
	case ".xlsx":
		return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
	}

	// Magic bytes kontrolü
	if len(data) >= 4 {
		if string(data[:4]) == "%PDF" {
			return "application/pdf"
		}
	}

	if len(data) >= 8 {
		if string(data[:8]) == "\x89PNG\r\n\x1a\n" {
			return "image/png"
		}
	}

	if len(data) >= 2 {
		if data[0] == 0xFF && data[1] == 0xD8 {
			return "image/jpeg"
		}
	}

	return "application/octet-stream"
}
