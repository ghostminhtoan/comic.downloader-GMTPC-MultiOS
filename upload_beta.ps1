# ==============================================================================
# Comic Downloader GMTPC - GitHub Release Beta Asset Uploader
# Repository: ghostminhtoan/comic.downloader-GMTPC-MultiOS
# Target Tag: beta
# ==============================================================================

param(
    [double]$MinSpeedMbps = 20.0,
    [int]$WarmupSeconds = 4,
    [int]$MaxAttempts = 30
)

if ($env:MIN_SPEED_MBPS) {
    [double]::TryParse($env:MIN_SPEED_MBPS, [ref]$MinSpeedMbps)
}

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$Host.UI.RawUI.WindowTitle = "Upload Release Beta - Comic Downloader GMTPC"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  UPLOAD RELEASE BETA - COMIC DOWNLOADER GMTPC MULTI-OS" -ForegroundColor Cyan
Write-Host "  Repo: ghostminhtoan/comic.downloader-GMTPC-MultiOS" -ForegroundColor DarkCyan
Write-Host "  Tag:  beta" -ForegroundColor DarkCyan
Write-Host "  Nguong toc do toi thieu: $MinSpeedMbps Mbps" -ForegroundColor Yellow
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
    $tokenFilePath = Join-Path $scriptDir "github_token.txt"
    if (Test-Path $tokenFilePath) {
        $token = (Get-Content -Path $tokenFilePath -Raw).Trim()
    }
}

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
        $proc.StandardInput.NewLine = "`n"
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
Write-Host "[4/4] Tien hanh upload $($uploadList.Count) file len Release beta (Yeu cau toc do >= 20 Mbps)..." -ForegroundColor Cyan

# Kich hoat TLS 1.2 va TLS 1.3
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12 -bor [System.Net.SecurityProtocolType]::Tls13

function Remove-ReleaseAssetByName {
    param(
        [string]$Owner,
        [string]$Repo,
        [int64]$ReleaseId,
        [string]$AssetName,
        [hashtable]$Headers
    )
    try {
        $assetsUrl = "https://api.github.com/repos/$Owner/$Repo/releases/$ReleaseId/assets"
        $assets = Invoke-RestMethod -Uri $assetsUrl -Headers $Headers -Method Get -ErrorAction Stop
        foreach ($a in $assets) {
            if ($a.name -ieq $AssetName) {
                Write-Host "  -> Phat hien asset cu/do dang '$($a.name)' (ID: $($a.id)). Dang xoa de ghi de..." -ForegroundColor DarkYellow
                Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/assets/$($a.id)" -Headers $Headers -Method Delete -ErrorAction Stop
                Write-Host "  -> Da xoa asset cu thanh cong." -ForegroundColor DarkGray
                Start-Sleep -Milliseconds 600
            }
        }
    } catch { }
}

function Upload-AssetWithSpeedGuard {
    param(
        [string]$UploadUri,
        [string]$FilePath,
        [string]$FileName,
        [string]$Token,
        [string]$Owner,
        [string]$Repo,
        [int64]$ReleaseId,
        [hashtable]$ApiHeaders,
        [double]$MinSpeedMbps = 20.0,
        [int]$WarmupSeconds = 4,
        [int]$MaxAttempts = 30
    )

    $fileInfo = Get-Item $FilePath
    $fileLength = $fileInfo.Length
    $fileSizeMB = [math]::Round($fileLength / 1MB, 2)

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Write-Host ""
        Write-Host "  -> [Lan thu $attempt/$MaxAttempts] Ket noi upload '$FileName' ($fileSizeMB MB)..." -ForegroundColor Cyan
        Write-Host "     (Nguong toc do yeu cau: >= $MinSpeedMbps Mbps. Tu dong huy & retry neu cham hon sau $WarmupSeconds giay)" -ForegroundColor DarkCyan

        # Xoa sach asset cung ten hoac do dang tren GitHub truoc khi upload
        Remove-ReleaseAssetByName -Owner $Owner -Repo $Repo -ReleaseId $ReleaseId -AssetName $FileName -Headers $ApiHeaders

        $request = $null
        $fileStream = $null
        $requestStream = $null
        $isAbortedDueToLowSpeed = $false

        try {
            $request = [System.Net.HttpWebRequest]::Create($UploadUri)
            $request.Method = "POST"
            $request.ContentType = "application/octet-stream"
            $request.Headers.Add("Authorization", "Bearer $Token")
            $request.Accept = "application/vnd.github+json"
            $request.UserAgent = "ComicDownloaderUploader/1.0"
            $request.ContentLength = $fileLength
            $request.Timeout = 900000
            $request.ReadWriteTimeout = 900000
            $request.AllowWriteStreamBuffering = $false
            $request.SendChunked = $false
            $request.KeepAlive = $false
            $request.ServicePoint.Expect100Continue = $false

            $fileStream = [System.IO.File]::OpenRead($FilePath)
            $requestStream = $request.GetRequestStream()

            $buffer = New-Object byte[] (128 * 1024) # 128 KB buffer
            $totalSent = 0
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $lastCheckTime = $stopwatch.ElapsedMilliseconds
            $lastSentBytes = 0
            $slowCounter = 0

            while (($bytesRead = $fileStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $requestStream.Write($buffer, 0, $bytesRead)
                $totalSent += $bytesRead

                $nowMs = $stopwatch.ElapsedMilliseconds
                $deltaMs = $nowMs - $lastCheckTime

                if ($deltaMs -ge 1000 -or $totalSent -eq $fileLength) {
                    $elapsedTotalSec = [math]::Max(0.1, $nowMs / 1000.0)
                    $deltaSec = [math]::Max(0.1, $deltaMs / 1000.0)
                    $bytesDelta = $totalSent - $lastSentBytes

                    $instantSpeedMbps = [math]::Round(($bytesDelta * 8.0) / ($deltaSec * 1000000.0), 2)
                    $avgSpeedMbps = [math]::Round(($totalSent * 8.0) / ($elapsedTotalSec * 1000000.0), 2)

                    $percent = [math]::Round(($totalSent / $fileLength) * 100.0, 1)
                    $sentMB = [math]::Round($totalSent / 1MB, 1)

                    $speedColor = if ($instantSpeedMbps -ge $MinSpeedMbps) { "Green" } else { "Yellow" }
                    Write-Host ("`r     [Tien do] $sentMB / $fileSizeMB MB ($percent%) | Toc do: $instantSpeedMbps Mbps | TB: $avgSpeedMbps Mbps    ") -NoNewline -ForegroundColor $speedColor

                    $lastCheckTime = $nowMs
                    $lastSentBytes = $totalSent

                    # KIEM TRA TOC DO: Sau thoi gian warm-up, neu toc do < MinSpeedMbps thi tu dong huy
                    if ($elapsedTotalSec -ge $WarmupSeconds -and $percent -lt 97.0) {
                        if ($instantSpeedMbps -lt $MinSpeedMbps) {
                            $slowCounter++
                            if ($slowCounter -ge 2 -or ($elapsedTotalSec -ge 6 -and $avgSpeedMbps -lt $MinSpeedMbps)) {
                                Write-Host ""
                                Write-Host "     [CANH BAO TOC DO] Toc do $instantSpeedMbps Mbps (TB: $avgSpeedMbps Mbps) < $MinSpeedMbps Mbps! Dang huy ket noi de thu lai..." -ForegroundColor Red
                                $isAbortedDueToLowSpeed = $true
                                break
                            }
                        } else {
                            $slowCounter = 0
                        }
                    }
                }
            }

            if ($isAbortedDueToLowSpeed) {
                try { $request.Abort() } catch { }
                try { $fileStream.Close() } catch { }
                try { $requestStream.Close() } catch { }
                Write-Host "     -> Nghi 2 giay de giai phong socket truoc khi thu lai..." -ForegroundColor DarkGray
                Start-Sleep -Seconds 2
                continue
            }

            $requestStream.Flush()
            $requestStream.Close()
            $fileStream.Close()

            $stopwatch.Stop()
            $totalSec = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)

            Write-Host ""
            Write-Host "     -> File da truyen xong, dang cho GitHub xac nhan luu asset..." -ForegroundColor DarkCyan

            $response = $request.GetResponse()
            $streamReader = New-Object System.IO.StreamReader($response.GetResponseStream())
            $responseJson = $streamReader.ReadToEnd()
            $streamReader.Close()
            $response.Close()

            $resObj = $responseJson | ConvertFrom-Json
            $finalSpeed = [math]::Round(($fileLength * 8.0) / ($totalSec * 1000000.0), 2)
            Write-Host "  [THANH CONG] Upload hoan tat '$FileName' trong $totalSec giay! (Toc do TB: $finalSpeed Mbps)" -ForegroundColor Green
            if ($resObj.browser_download_url) {
                Write-Host "               URL: $($resObj.browser_download_url)" -ForegroundColor DarkGray
            }
            return $true

        } catch {
            if ($isAbortedDueToLowSpeed) {
                continue
            }
            Write-Host ""
            Write-Host "  [LOI KET NOI] $($_.Exception.Message)" -ForegroundColor Red
            try { if ($request) { $request.Abort() } } catch { }
            Start-Sleep -Seconds 3
        } finally {
            if ($fileStream) { try { $fileStream.Dispose() } catch { } }
            if ($requestStream) { try { $requestStream.Dispose() } catch { } }
        }
    }

    Write-Host "  [THAT BAI] Khong the upload '$FileName' sau $MaxAttempts lan thu dat toc do >= $MinSpeedMbps Mbps!" -ForegroundColor Red
    return $false
}

$successCount = 0

for ($i = 0; $i -lt $uploadList.Count; $i++) {
    $item = $uploadList[$i]
    $step = "[$($i + 1)/$($uploadList.Count)]"
    Write-Host ""
    Write-Host "$step Chuan bi upload file: $($item.Name) ($($item.SizeMB) MB)" -ForegroundColor Yellow

    $uploadUri = "https://uploads.github.com/repos/$owner/$repo/releases/$($release.id)/assets?name=$($item.Name)"

    $ok = Upload-AssetWithSpeedGuard -UploadUri $uploadUri -FilePath $item.Path -FileName $item.Name -Token $token -Owner $owner -Repo $repo -ReleaseId $release.id -ApiHeaders $headers -MinSpeedMbps $MinSpeedMbps -WarmupSeconds $WarmupSeconds -MaxAttempts $MaxAttempts

    if ($ok) {
        $successCount++
        Write-Host "  [OK] File $($item.Name) da hoan tat. Chuyen sang file tiep theo..." -ForegroundColor Green
    } else {
        Write-Host "  [BO QUA] File $($item.Name) khong the upload dat toc do mong muon." -ForegroundColor Red
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

