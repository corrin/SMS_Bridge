# Builds master and publishes it as the GitHub release that install_bridge.ps1 installs:
#   powershell -ExecutionPolicy Bypass -File Scripts\publish_release.ps1
# Runs where JustRemotePhone Call Centre is installed: the bridge compiles against its SDK, which
# the release leaves out. Builds from a fresh clone, so local files can't end up in the release.
$ErrorActionPreference = 'Stop'
function Run { $ErrorActionPreference = 'Continue'; & $args[0] @($args | Select-Object -Skip 1); if ($LASTEXITCODE) { throw "failed (exit $LASTEXITCODE): $args" } }
$src = Join-Path $env:TEMP 'SMS_Bridge-release'
if (Test-Path $src) { Remove-Item -Recurse -Force $src }
Run git clone --quiet https://github.com/corrin/SMS_Bridge.git $src
$tag = git -C $src rev-parse --short=7 HEAD
Run dotnet publish "$src\SMS_Bridge.csproj" -c Release -r win-x64 --self-contained true -o "$src\out" -nologo -v q
Compress-Archive "$src\out\*" "$src\SMS_Bridge-$tag.zip"
Run gh release create $tag "$src\SMS_Bridge-$tag.zip" --repo corrin/SMS_Bridge --title "SMS_Bridge $tag" --notes 'Install or upgrade on the server with Scripts/install_bridge.ps1.'
Remove-Item -Recurse -Force $src
Write-Host "Released $tag." -ForegroundColor Green
