# Windows 原生追蹤器實測 / Native tracker validation

候選版：**1.1.0-rc.1**。既有 v1.0.0 Release 未變更。

2026-11-01，Windows build 26300、Intel Wireless Bluetooth、原生驅動 22.150.0.6。控制器回報 Peripheral／Extended 支援，Extended 最大長度 160。測試使用未註冊、零填充研究資料，沒有載入任何真實帳號／追蹤器秘密。

| 要求 / Request | 本機 API 結果 / Result | 結論 / Meaning |
| --- | --- | --- |
| Publisher，28-byte Legacy | Started／Stopped | API lifecycle accepted |
| Publisher，完整 31-byte Apple 格式 Legacy | Started／Stopped | API accepts full request; RF/address unverified |
| GATT FEAA，21-byte ServiceData（Frame + 20-byte EID） | StartedWithoutAllAdvertisementData | Failed: incomplete FHN data |
| GATT FEAA，22-byte ServiceData | StartedWithoutAllAdvertisementData | Failed: incomplete data |
| GATT FEAA，21-byte／33-byte，要求 secondary 1M PHY | StartedWithoutAllAdvertisementData | Extended request did not resolve completeness |
| GATT FEAA，短 ServiceData 控制組 | 首輪短資料曾 Started；應用後端重測回報資料不足 | Not reliably accepted; inspect every individual start result |

[應用後端原始結果（已移除裝置識別碼）](validation/windows-native-2026-11-01.json)保留每次狀態。此 Windows 組態可能先送出 `Aborted: Success` 再送出實際結果；後端會有限時等待明確 Started／資料不足／錯誤，過渡通知不計為接受。每次實測都會停止並釋放廣播。

以上結果是**這台電腦與當時 Windows 狀態**的 API 觀測，不是所有 Windows 配接器的普遍上限。沒有外部接收器／手機封包、實際 MAC、Primary／Scan Response 或可解密帳號定位回報，因此兩條路線均未標示「真實追蹤已驗證」。

## 已完成 / Implemented

- Publisher 與 GATT 分開驗證；Legacy 28-byte 保守預算與 31-byte API 測試上限分離。
- 追蹤工作階段、一次一種協定、Google EID 推導與更新、DPAPI 私鑰／憑證、系統匣、睡眠／喚醒檢查與恢復。
- 獨立 ITrackingProvider、HTTPS 桌面 Client、自架 SQLite／API／Caddy、擁有者登入、可撤銷 Token、真實報告 Provider、Windows／手機共用地圖與歷史。
- Apple GCM 報告驗證及有號座標；Google 註冊與輪替識別資料更新。一般日誌／匯出不包含秘密金鑰。

## 尚待環境驗證 / Not yet verified

1. 手機掃描器核對 UUID 寬度、完整資料、Legacy／Extended、主廣播／Scan Response、Windows 真正位址。應用不會把自身輸入當成外部證據。
2. Apple 金鑰導出的靜態隨機位址必須與手機實際觀測相符；Windows 無法指定該位址時保持不相容狀態。
3. 公開 VPS／HTTPS 部署、本人帳號授權與可解密的實際網路報告。離線密碼學測試不是帳號／網路驗證。
4. 實體睡眠／喚醒與系統匣操作仍須在互動式使用者桌面確認；自動測試驗證狀態與恢復邏輯。沒有為測試而讓使用者電腦進入睡眠。

Google 的每個新 EID 都會清除舊封包／位置驗證，只有匹配目前廣播 EID 的可解密網路報告才能通過最後一層。舊 EID 報告、只有自身回報且缺少 EID 證據、Scan Response 資料、Extended 傳輸、錯誤 UUID 寬度或 Apple 位址不匹配，均不能取得完整驗證。

## 重跑與手機擷取 / Repeat and capture

```powershell
BLEStudio.exe --tracker-hardware-test --output .\tracker-native-results
```

此診斷只短暫傳送未註冊測試資料。完整追蹤器測試請在追蹤器頁建立自有身分、啟動、以獨立手機工具擷取，依 [擷取欄位範例](tracker-observation.example.json) 匯入。欄位中的示範值不是本專案實測證據；請替換所有欄位。

若工具無法明確區分主廣播及 Scan Response，或不能證明 Legacy 類型，就不能通過封包關卡。Google 應在 1024 秒輪替前後再次擷取。只看 Publisher Started、GATT Started、手機看見裝置名稱或 RSSI，均不足以證明網路追蹤。

## English

The full 31-byte Apple-format legacy request was accepted by Publisher on this PC. Complete FEAA/FHN ServiceData requests returned `StartedWithoutAllAdvertisementData`, including secondary-PHY requests. Short GATT controls were not consistently complete across runs. The app rejects incomplete results and preserves diagnostics. No external RF packet/address or decrypted real-account location report was verified. Apple address matching and Windows-managed Google address rotation remain compatibility gates. The candidate is not a verified AirTag/Find Hub replacement.

API tests can be repeated with the command above. Import an independently captured phone packet with source, capture time, observed address/type, explicit Legacy advertisement type and raw primary AD bytes. Scan Response and Extended packets do not pass the legacy primary-frame gate. A matching decrypted current-EID report is required for Google verification. Account authorization and deployment instructions are in [English](../deploy/README.en.md) / [繁體中文](../deploy/README.md).

Primary references: [Microsoft GATT ServiceData](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattserviceprovideradvertisingparameters.servicedata), [Microsoft Publisher](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementpublisher), [Google FHN](https://developers.google.com/nearby/fast-pair/specifications/extensions/fmdn), [OpenHaystack](https://github.com/seemoo-lab/openhaystack), [pinned GoogleFindMyTools](https://github.com/leonboe1/GoogleFindMyTools/tree/660be5395702ff7f1a6174f4ec4c865a57c7b527), [pinned Macless](https://github.com/dchristl/macless-haystack/tree/0bda271b8bd0cc37194dfba1845cd48954cba4cc).
