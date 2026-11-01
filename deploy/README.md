# 自架位置服務（繁體中文）

[English](README.en.md)

本服務提供擁有者登入、可撤銷的桌面憑證、SQLite 位置歷史、手機／Windows 瀏覽器地圖，以及 Apple／Google 報告 Provider。**藍牙廣播只由 Windows 電腦負責**；Linux／VPS 不需要藍牙，也不會燒錄任何裝置。

## 部署

需求：Linux／VPS、Docker Engine、Docker Compose v2、Python 3，以及指向這台 VPS 的公開 DNS 名稱。開放 80/tcp、443/tcp（443/udp 選用）。

```sh
cd deploy
python3 setup.py
docker compose up -d --build
docker compose ps
```

`setup.py` 會互動要求網域與至少 16 字元的擁有者密碼，產生 `secrets/master_key`、`secrets/owner_password`、`.env`、私有資料目錄及 Apple 端點憑證。不會覆寫既有設定。以瀏覽器開啟 `https://您的網域`，使用 `owner` 登入。Caddy 自動申請公開 HTTPS 憑證；DNS／防火牆及憑證申請必須成功後才能提供遠端存取。

在「擁有者設定與桌面憑證」下載 JSON，於 Windows 追蹤器頁按「匯入桌面憑證」。匯入後請刪除下載的明文檔；Windows 使用 CurrentUser DPAPI 儲存憑證。桌面憑證 90 天到期，也可在網頁立即撤銷。桌面憑證不能管理 Provider 帳號授權或核發其他憑證。

## Google 自有帳號授權

首次授權需在**您自己的電腦**使用 Chrome、Python 及自有 Google／Android 帳號完成。帳號登入與手機鎖定碼由您操作，不要貼到聊天、Issue 或日誌。

```sh
git clone https://github.com/leonboe1/GoogleFindMyTools.git
git -C GoogleFindMyTools checkout 660be5395702ff7f1a6174f4ec4c865a57c7b527
python -m venv google-auth-env
# Windows: google-auth-env\Scripts\activate
# Linux/macOS: . google-auth-env/bin/activate
python -m pip install -r GoogleFindMyTools/requirements.txt
python scripts/google-authorize.py --tools GoogleFindMyTools --output google-authorization.json
```

將 `google-authorization.json` 透過 HTTPS 擁有者頁匯入後刪除。這個輔助程式只取得帳號授權，不會註冊 MCU，也不會產生韌體。首次授權的上游工具仍可能受 Google 帳號政策、Chrome 版本及 API 變動影響。

在 Windows 建立 Google 身分，再按「註冊身分」。伺服器以同一個 EIK／配對時間登記自有測試身分，發布未來約四天、每 1024 秒變更的 EID 識別資料，每五分鐘更新及查詢。Windows 自行計算相同 EID。未完成授權、註冊失敗或位置查詢超時會顯示明確狀態，不產生替代座標。

Google Adapter 使用上游開發用 Model ID 與自訂金鑰格式。**並非 Fast Pair 認證配件**；官方 Android Find Hub 對這種自訂金鑰格式的顯示／解密不保證相容。手機應開啟本服務的 HTTPS 網頁，與 Windows 使用同一份位置資料。DULT、響鈴及完整配件 GATT 行為尚未實作。Windows 自主管理位址的限制仍保留，不能因註冊完成就宣稱追蹤成功。

## Apple 自有帳號授權

Apple Provider 透過私有 Macless Haystack 端點查詢加密報告，並以您自己的 P-224 私鑰驗證 GCM 標籤及解密位置。端點與 Anisette 只在 Compose 私有網路可達，不公開 6176／6969 連接埠。

```sh
cd deploy
docker compose --profile apple build
docker compose --profile apple up -d anisette
docker compose --profile apple run --rm apple
```

依上游互動提示，親自輸入 Apple ID 與簡訊雙重認證。完成授權並進入提供服務階段後按 Ctrl+C，再啟動常駐端點：

```sh
docker compose --profile apple up -d
```

將 `apple-data/config.ini` 的 **endpoint_user／endpoint_pass** 透過 HTTPS 擁有者頁輸入。這是私有端點的存取憑證，**不是 Apple ID 密碼**。Apple 帳號驗證資料存於 `apple-data/auth.json`；需保護整個資料目錄。上游／Apple 若拒絕授權（例如 503），服務會回報失敗，不會繞過帳號保護。

Windows 建立／匯入 Apple 身分後可上傳私鑰到自己的服務。但如果實際廣播位址與公開金鑰不匹配，Windows 原生廣播仍不具網路相容性；報告解密功能無法補足這個無線限制。

## 位置、歷史與保護

- 無可解密報告時顯示「尚無定位回報」。地圖在有真實座標後才載入；座標、時間、來源與誤差均保留。Apple 經緯度使用有號整數解碼，無效 GCM 標籤會拒絕。
- 歷史保留 30 天；每五分鐘向來源查詢，網頁每分鐘讀取快取。網路失效保留最後回報及其時間，並顯示錯誤狀態。
- 伺服器的身分私鑰、Google 授權與端點憑證用 AES-256-GCM 加密，密文與用途綁定。Master Key 透過 Docker Secret 唯讀掛載，與 SQLite 分離；資料目錄及設定由 setup 建成僅擁有者可讀。
- Macless／Anisette 的第三方帳號檔案由私有目錄／Volume 與主機磁碟保護；並非本服務 AES-GCM 資料庫的一部分。請使用 VPS 磁碟加密及加密備份，限制系統管理員存取。
- 正常 API／Worker 日誌不輸出帳號、金鑰或請求內容。Google Worker 的暫存授權目錄權限 0700，工作完成刪除；請求逾時會結束 Worker。
- 擁有者登入使用 Argon2、登入限速、Secure／HttpOnly／SameSite=Strict Cookie、CSRF 標頭。Token 僅儲存 SHA256；桌面 Token 可以撤銷。
- 地圖使用附授權的 Leaflet 與 OpenStreetMap 標準圖磚，顯示來源標示；載入地圖會連線 OSM 圖磚服務。沒有離線預抓或批次下載。

備份時應保留 `data/`、`secrets/`、`.env`、`apple-data/` 及 Anisette Volume，使用加密備份且限制權限。遺失 Master Key 將無法解密既有身分。**不要將設定完成的目錄、帳號檔案或備份重新打包／上傳 GitHub。**

本次已完成離線 API／密碼學測試與 Compose 組態驗證。公開 VPS、HTTPS 憑證申請、真實帳號授權和網路定位回報需在您的環境驗證；本機的完整 FHN 原生廣播測試未通過，詳見 [Windows 實測](../docs/native-tracking-validation.md)。
