# ==============================================================================
# Comic Downloader GMTPC - GitHub Release Beta Asset Uploader
# Repository: ghostminhtoan/comic.downloader-GMTPC-MultiOS
# Target Tag: beta
# ==============================================================================

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$Host.UI.RawUI.WindowTitle = "Upload Release Beta - Comic Downloader GMTPC"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  UPLOAD RELEASE BETA - COMIC DOWNLOADER GMTPC MULTI-OS" -ForegroundColor Cyan
Write-Host "  Repo: ghostminhtoan/comic.downloader-GMTPC-MultiOS" -ForegroundColor DarkCyan
Write-Host "  Tag:  beta" -ForegroundColor DarkCyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }
$publishDir = Join-Path $scriptDir "publish"

if (-not (Test-Path $publishDir)) {
    # Thu kiem tra thu muc publish o thu muc cha neu chay tu thu muc con
    $parentPublish = Join-Path (Split-Path -Parent $scriptDir) "publish"
    if (Test-Path $parentPublish) {
        $publishDir = $parentPublish
    } else {
        $avaloniaPublish = Join-Path $scriptDir "Comic Downloader GMTPC AVALONIA\publish"
        if (Test-Path $avaloniaPublish) {
            $publishDir = $avaloniaPublish
        }
    }
}

Write-Host "[1/4] Kiem tra cac goi cai dat trong thu muc publish..." -ForegroundColor Yellow
Write-Host "      Duong dan: $publishDir" -ForegroundColor DarkGray

$targetFiles = @(
    "com.CompanyName.ComicDownloaderGMTPC-Signed.apk",
    "ComicDownloaderGMTPC.Desktop.exe",
    "ComicDownloaderGMTPC-linux-x64.tar.gz"
)

$missingFiles = @()
$uploadList = @()

foreach ($fileName in $targetFiles) {
    $fullPath = Join-Path $publishDir $fileName
    if (Test-Path $fullPath) {
        $fileObj = Get-Item $fullPath
        $sizeMB = [math]::Round($fileObj.Length / 1MB, 2)
        $uploadList += [PSCustomObject]@{
            Name = $fileName
            Path = $fullPath
            SizeMB = $sizeMB
            SizeBytes = $fileObj.Length
        }
        Write-Host "  [OK] Tim thay: $fileName ($sizeMB MB)" -ForegroundColor Green
    } else {
        $missingFiles += $fileName
        Write-Host "  [MISSING] Khong tim thay: $fileName" -ForegroundColor Red
    }
}

if ($missingFiles.Count -gt 0) {
    Write-Host ""
    Write-Host "Canh bao: Thieu $($missingFiles.Count) file! Ban can chay build-avalonia.bat truoc de dong goi day du." -ForegroundColor Red
    $choice = Read-Host "Ban co muon tiep tuc upload nhung file dang co san khong? (Y/N)"
    if ($choice -ne "Y" -and $choice -ne "y") {
        Write-Host "Da huy thao tac upload." -ForegroundColor Yellow
        exit 1
    }
}

if ($uploadList.Count -eq 0) {
    Write-Host "[ERROR] Khong co file nao de upload! Vui long chay build-avalonia.bat truoc." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "[2/4] Xac thuc tai khoan GitHub..." -ForegroundColor Yellow

$token = $env:GITHUB_TOKEN

if (-not $token) {
    # Thu lay token tu Windows Git Credential Manager
    try {
        $pinfo = New-Object System.Diagnostics.ProcessStartInfo
        $pinfo.FileName = "git"
        $pinfo.Arguments = "credential fill"
        $pinfo.UseShellExecute = $false
        $pinfo.RedirectStandardInput = $true
        $pinfo.RedirectStandardOutput = $true
        $pinfo.CreateNoWindow = $true
        $proc = [System.Diagnostics.Process]::Start($pinfo)
        $proc.StandardInput.WriteLine("protocol=https")
        $proc.StandardInput.WriteLine("host=github.com")
        $proc.StandardInput.WriteLine("")
        $proc.StandardInput.Close()
        $outText = $proc.StandardOutput.ReadToEnd()
        $proc.WaitForExit()

        $lines = $outText -split "`n"
        foreach ($line in $lines) {
            if ($line -like "password=*") {
                $token = $line.Substring(9).Trim()
                break
            }
        }
    } catch { }
}

if (-not $token) {
    $tokenFilePath = Join-Path $scriptDir "github_token.txt"
    if (Test-Path $tokenFilePath) {
        $token = (Get-Content -Path $tokenFilePath -Raw).Trim()
    }
}

if (-not $token) {
    Write-Host "[CANH BAO] Khong tim thay GitHub token trong Git Credential Manager!" -ForegroundColor Yellow
    Write-Host "Vui long nhap GitHub Personal Access Token (PAT) co quyen repo/write:releases:" -ForegroundColor Yellow
    $token = Read-Host -AsSecureString
    $token = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto([System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($token))
}

if (-not $token) {
    Write-Host "[ERROR] Thieu GitHub Token, khong the xac thuc voi GitHub API." -ForegroundColor Red
    exit 1
}

$maskedToken = if ($token.Length -gt 8) { $token.Substring(0, 4) + "..." + $token.Substring($token.Length - 4) } else { "***" }
Write-Host "  [OK] Da xac thuc thanh cong voi GitHub Token ($maskedToken)" -ForegroundColor Green

$headers = @{
    "Authorization" = "Bearer $token"
    "User-Agent" = "ComicDownloaderUploader/1.0"
    "Accept" = "application/vnd.github+json"
}

$owner = "ghostminhtoan"
$repo = "comic.downloader-GMTPC-MultiOS"
$tag = "beta"

Write-Host ""
Write-Host "[3/4] Kiem tra Release tag '$tag' tren GitHub..." -ForegroundColor Yellow

$releaseUrl = "https://api.github.com/repos/$owner/$repo/releases/tags/$tag"
$release = $null

try {
    $release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers -Method Get -ErrorAction Stop
    Write-Host "  [OK] Tim thay Release '$($release.name)' (ID: $($release.id))" -ForegroundColor Green
} catch {
    Write-Host "  -> Release '$tag' chua ton tai. Dang tao moi release '$tag'..." -ForegroundColor Yellow
    $createBody = @{
        tag_name = $tag
        name = "Release $tag"
        body = "Ban phat hanh thu nghiem tu dong (Automated Beta Release) cho Comic Downloader GMTPC Multi-OS.`n`nBao gom cac goi cai dat standalone: Windows (.exe), Linux (.tar.gz) va Android (.apk)."
        draft = $false
        prerelease = $true
    } | ConvertTo-Json

    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases" -Headers $headers -Method Post -Body $createBody -ContentType "application/json" -ErrorAction Stop
        Write-Host "  [OK] Da tao thanh cong Release '$tag' (ID: $($release.id))" -ForegroundColor Green
    } catch {
        Write-Host "[ERROR] Khong the tao release moi: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "[4/4] Tien hanh upload $($uploadList.Count) file len Release beta..." -ForegroundColor Cyan

# Lay danh sach asset hien tai
$existingAssets = @()
try {
    $existingAssets = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases/$($release.id)/assets" -Headers $headers -Method Get -ErrorAction Stop
} catch { }

$successCount = 0

for ($i = 0; $i -lt $uploadList.Count; $i++) {
    $item = $uploadList[$i]
    $step = "[$($i + 1)/$($uploadList.Count)]"
    Write-Host ""
    Write-Host "$step Bat dau upload: $($item.Name) ($($item.SizeMB) MB)" -ForegroundColor Yellow

    # Kiem tra neu asset da ton tai thi xoa de upload moi ghi de
    $matchAsset = $existingAssets | Where-Object { $_.name -ieq $item.Name }
    if ($matchAsset) {
        Write-Host "  -> Phat hien asset cu (ID: $($matchAsset.id)). Dang xoa de ghi de..." -ForegroundColor DarkYellow
        try {
            Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases/assets/$($matchAsset.id)" -Headers $headers -Method Delete -ErrorAction Stop
            Write-Host "  -> Da xoa asset cu thanh cong." -ForegroundColor DarkGray
        } catch {
            Write-Host "  -> Khong the xoa asset cu: $($_.Exception.Message)" -ForegroundColor DarkYellow
        }
    }

    # Upload asset moi
    $uploadUri = "https://uploads.github.com/repos/$owner/$repo/releases/$($release.id)/assets?name=$($item.Name)"
    $uploadHeaders = @{
        "Authorization" = "Bearer $token"
        "User-Agent" = "ComicDownloaderUploader/1.0"
        "Accept" = "application/vnd.github+json"
        "Content-Type" = "application/octet-stream"
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $uploaded = $false
    $maxRetries = 3

    for ($retry = 1; $retry -le $maxRetries; $retry++) {
        try {
            if ($retry -gt 1) {
                Write-Host "  -> Thu lai lan $retry/$maxRetries..." -ForegroundColor DarkYellow
                Start-Sleep -Seconds 3
            }

            Write-Host "  -> Dang truyen file len GitHub..." -ForegroundColor Cyan
            $uploadResp = Invoke-RestMethod -Uri $uploadUri -Headers $uploadHeaders -Method Post -InFile $item.Path -TimeoutSec 900 -ErrorAction Stop
            $stopwatch.Stop()
            $elapsedSec = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)

            Write-Host "  [OK] Upload thanh cong trong $elapsedSec giay!" -ForegroundColor Green
            Write-Host "       Download URL: $($uploadResp.browser_download_url)" -ForegroundColor DarkGray
            $uploaded = $true
            $successCount++
            break
        } catch {
            Write-Host "  [CANH BAO] Loi khi upload ($($_.Exception.Message))" -ForegroundColor Red
        }
    }

    if (-not $uploaded) {
        Write-Host "  [THAT BAI] Khong the upload $($item.Name) sau $maxRetries lan thu!" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "========================================================" -ForegroundColor Cyan
if ($successCount -eq $uploadList.Count) {
    Write-Host "  [HOAN TAT] Da upload thanh cong $successCount/$($uploadList.Count) file len Release beta!" -ForegroundColor Green
} else {
    Write-Host "  [KET QUA] Da upload $successCount/$($uploadList.Count) file." -ForegroundColor Yellow
}
Write-Host "  Xem Release tai:" -ForegroundColor White
Write-Host "  $($release.html_url)" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

