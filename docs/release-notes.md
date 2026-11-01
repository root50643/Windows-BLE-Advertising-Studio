## 繁體中文

Windows BLE Advertising Studio v1.0.0 提供七頁 WPF 介面、Windows BLE 廣播／掃描、
Hex／ASCII 與 AD 解析、JSON 設定檔／修訂歷程、RSSI 曲線、CSV／JSON 匯出、
DPAPI 加密研究金鑰庫、Apple／Google 封包研究與明確標示的模擬模式。
README 以繁體中文為主，另附英文版；程式介面目前為英文。

下載方式：

- **ZIP**：建議使用，內含自包含 Windows x64 EXE、中英文文件、範例設定檔與授權聲明。
- **BLEStudio.exe**：可獨立執行的單檔版本，不需要另外安裝 .NET Runtime。
- **SHA256SUMS.txt**：核對 EXE 與 ZIP 的 SHA256；使用下載檔名，請放在同一目錄比對。
- **build-info.json**：EXE 版本、大小、建置時間與 SHA256。

最低需求為 Windows 10 組建 19041、x64；實際藍牙操作取決於配接器與驅動。
本版本未經程式碼簽章。發布流程會執行 81 項 xUnit 測試及 16 項 WPF GUI 模擬檢查。
本機曾驗證一般廣播啟停與實際掃描；不代表已驗證任意機器、空中封包或定位網路。

研究限制：Apple 金鑰導出位址、Google 必要 Service Data 廣播、帳號／配件註冊、
EID 產生、完整 DULT 及定位回報流程均未實作。Publisher 的 Started 不是定位成功，
也不證明實際空中資料與輸入完全一致。請只在已獲授權且測試者知情的環境使用。

## English

First release of the Windows BLE advertising, scanning and protocol-research
workbench. Download the ZIP for the complete package or the self-contained
standalone EXE. No separately installed .NET runtime is required. Verify both
assets against SHA256SUMS.txt; build-info.json records EXE version, size and hash.
Documentation is available in Traditional Chinese and English; the GUI is English.

Requires Windows x64, build 19041 or later, and compatible hardware/drivers for
radio operations. Binaries are unsigned. The release pipeline runs 81 xUnit
tests and 16 mock WPF GUI checks; local radio results are documented separately.

Apple/Google features analyze packet formats. Accessory provisioning, network
registration, EID derivation, DULT and location-report retrieval are not implemented.
Windows Publisher Started does not establish RF byte equality or network participation.
