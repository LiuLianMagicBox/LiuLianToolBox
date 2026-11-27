# LiuLianBot 的 ToolBox 擴充

此擴充沿用 LiuLianBot 現有的 Discord 工作階段、slash command 註冊及私人語音頻道擁有權資料。桌面端只持有自己的裝置憑證。主機管理員可在 LiuLianToolBox 根目錄使用 Python 3.11、Git 及符合 LiuLianBot `go.mod` 的 Go 工具鏈建置。

```sh
python3 integrations/liulianbot/build.py ../LiuLianBot
```

建置工具會從指定 checkout 的 `HEAD` 匯出已提交的 Go 原始碼，在暫存目錄接入擴充，然後產生 `artifacts/liulianbot-toolbox`。已驗證的 LiuLianBot 提交為 `d565e488`。擴充掛接於 Bot 初始化、HTTP listener、指令註冊和互動分派，升級上游時可用下方的整合測試檢查相容性。

## 主機設定

1. 複製 `config.example.json` 到主機上的私人設定目錄。將 `LIULIAN_TOOLBOX_CONFIG` 指向這份檔案。相對的 `stateFile` 路徑以設定檔所在目錄為準，服務帳戶需要讀寫權限。此檔案會保存已連結電腦、配對、鬧鐘、使用者設定及留言回條。
2. 在 `users` 以 Discord 使用者 ID 分別授予 `idle` 和 `mailbox`。需要接收電郵時，在管理員已確認地址屬於該收件人後填寫 `email`。桌面端只能選擇收件方式。權限在設定、聯絡及留言時重新讀取，移除權限會在下一次同步隱藏相關選單。
3. 在既有 Bot 主機執行下方命令，讓擴充與既有 Bot 共用一個 Discord 工作階段。沿用原專案的 Discord 及 MySQL 設定。服務管理器應啟動此二進位檔的 `serve` 子命令。

```sh
export LIULIAN_TOOLBOX_CONFIG=/etc/liulianbot/toolbox.json
./artifacts/liulianbot-toolbox -root /path/to/LiuLianBot serve
```

將 HTTPS 反向代理指向設定中的回環位址，例如 `127.0.0.1:48750`。將服務的 HTTPS 根網址交給桌面使用者，例如 `https://toolbox.example.org`。代理亦可使用路徑前綴，轉送時移除該前綴，使 Bot 收到 `/v1/…`。請在代理保留 `Authorization` 標頭，並讓請求可等待至少 25 秒。用戶端約每兩秒同步，服務在十五秒未收到同步後會將該電腦視為離線。

## MailServer

Bot 使用驗證憑證的 SMTPS 向 MailServer 提交郵件，預設使用 465 埠。設定 `smtp.host`、寄件地址 `from`、登入帳戶 `username` 及接收 DM 中轉的專用信箱 `relayInbox`。SMTP 密碼由 Bot 的環境變數 `LIULIAN_TOOLBOX_SMTP_PASSWORD` 提供。寄件地址須獲 MailServer 准許使用。

- **電郵**寄往 `users` 內該 Discord 帳戶的已驗證地址。SMTP 接受後回條顯示已排入佇列。如果伺服器提供 DSN，Bot 會要求成功、延遲及失敗通知。請讓寄往信封寄件人 `from` 的 DSN 進入下方的專用信箱。收到相符的投遞通知後會更新回條。未提供終端通知的郵件只顯示最後已知狀態。[SMTP DSN](https://datatracker.ietf.org/doc/html/rfc3461) [投遞報告格式](https://datatracker.ietf.org/doc/html/rfc3464)
- **Discord 私訊**先寄往 `relayInbox`。MailServer 收到郵件後，轉送程式才會將原文及該封郵件的專用憑據交回 Bot。Bot 核對原文、對應收件人及當前留言箱權限，再發送 DM。已確認的重複回條會回覆成功。Discord 結果不明時會保留待核實狀態以供追查。

在可存取專用信箱的主機提供 `TOOLBOX_IMAP_HOST`、`TOOLBOX_IMAP_USER`、`TOOLBOX_IMAP_PASSWORD` 及 `TOOLBOX_URL`，然後定期執行轉送程式。它透過 IMAPS 993 讀取未讀郵件，Bot 確認後才標為已讀。可使用相同的專用信箱接收 DM 中轉與 DSN。SMTP 密碼、IMAP 密碼及真實收件地址均應放在部署環境。

```sh
python3 integrations/mailserver/forward_mail.py
```

## 整合測試

在 Linux 或 macOS 安裝 Go 的 race detector 所需 C 編譯器後，可在根目錄執行以下命令。

```sh
python3 integrations/liulianbot/build.py ../LiuLianBot --test --dotnet dotnet
```

測試使用本機 Discord 回應、臨時 SMTP 服務及真正的 Go HTTP 處理流程。提供 `LIULIANBOT_TEST_MYSQL` 即可一併執行私人頻道擁有權轉移與權限撤銷測試。該變數使用 Go MySQL DSN 格式，須指向容許建立和刪除臨時 schema 的專用測試實例。未設定時只有這項資料庫測試會略過。
