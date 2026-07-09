# Hope MSIX 商店包

与根目录 `setup.iss`（Inno Setup → `Hope_Setup.exe`）并行存在，供 **微软应用商店** 分发。

## 产物

| 文件 | 用途 |
|------|------|
| `Hope_<版本>_x64.msix` | 商店 Package URL（x64） |
| `Hope_<版本>_x64.msixupload` | Partner Center 上传包（zip：msix + AppxManifest） |
| `Hope_<版本>_x64.msix.sha256` | 校验摘要 |

GitHub Release 仍保留 **`Hope_Setup.exe`**（官网 / 自动更新），互不影响。

## 本地打包

```powershell
# 先编译 stage（与 CI 相同）
go build -ldflags="-s -w -H=windowsgui" -o stage\hope-headless.exe .\src\headless
dotnet publish src\win-desktop\Hope.Desktop.csproj -c Release -r win-x64 --self-contained false -o stage

# 打 MSIX
pwsh scripts/pack-msix.ps1 -StageDir stage -OutputDir dist -Version 0.13.83
```

## Partner Center 身份

上架前在 **Partner Center → 应用 → 产品身份** 查看：

- **包身份名称** → 环境变量 `HOPE_MSIX_IDENTITY_NAME`（默认 `CooloiStudio.Hope`）
- **发布者** → 环境变量 `HOPE_MSIX_PUBLISHER`（`CN=...` 完整 DN）

在 GitHub Actions Secrets 中配置上述两项后，CI 生成的清单会与商店预留身份一致。

## 商店「Manage packages」

为 **x64** 填写 GitHub Release 资产直链，例如：

`https://github.com/CooloiStudio/Hope/releases/download/v0.13.83/Hope_0_13_83_x64.msix`

或使用 `.msixupload` 文件上传。勾选 **由 Microsoft 签名** 时，可上传未签名 MSIX，由商店重签。
