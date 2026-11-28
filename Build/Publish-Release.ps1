$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'FileViewer.csproj'
$work = Join-Path ([IO.Path]::GetTempPath()) ('UnitManager-Release-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $work 'publish'
$installer = Join-Path $work 'installer'
$setup = Join-Path $work 'setup'

try {
    dotnet publish $project -c Release -r win-x64 --self-contained false -o $publish -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw 'App publishing failed.' }

    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $publish 'Unit Manager.dll')).Version
    $version = $assemblyVersion.ToString(3)
    $name = "Unit-Manager-$version-win-x64"
    dotnet build (Join-Path $root 'Installer/UnitManager.wixproj') -c Release -o $installer `
        "-p:AppVersion=$version" "-p:PublishDir=$publish"
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

    $runtimeFeed = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $desktop = ($runtimeFeed.releases | Where-Object { $_.windowsdesktop.version -eq $runtimeFeed.'latest-runtime' } |
        Select-Object -First 1).windowsdesktop
    $runtime = $desktop.files | Where-Object { $_.rid -eq 'win-x64' -and $_.url.EndsWith('.exe') } |
        Select-Object -First 1
    if (!$runtime -or $runtime.hash -notmatch '^[a-fA-F0-9]{128}$') {
        throw 'The Microsoft feed did not provide a valid x64 Desktop Runtime installer.'
    }
    $runtimeFile = Join-Path $work $runtime.name
    Invoke-WebRequest -Uri $runtime.url -OutFile $runtimeFile
    if ((Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA512).Hash -ne $runtime.hash) {
        throw 'The .NET Desktop Runtime download did not match the Microsoft feed checksum.'
    }
    $runtimeSize = (Get-Item -LiteralPath $runtimeFile).Length
    $msi = (Get-ChildItem -LiteralPath $installer -Recurse -File -Filter 'UnitManager.msi' | Select-Object -First 1).FullName
    if (!$msi) { throw 'The installer build did not produce UnitManager.msi.' }
    dotnet build (Join-Path $root 'Installer/Bootstrapper/Setup.wixproj') -c Release -o $setup `
        "-p:AppVersion=$version" "-p:MsiPath=$msi" "-p:RuntimeName=$($runtime.name)" `
        "-p:RuntimeUrl=$($runtime.url)" "-p:RuntimeSize=$runtimeSize" "-p:RuntimeHash=$($runtime.hash)"
    if ($LASTEXITCODE -ne 0) { throw 'Setup bundle build failed.' }

    $zip = Join-Path $work "$name.zip"
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
    Copy-Item -LiteralPath $zip -Destination $PSScriptRoot -Force
    Copy-Item -LiteralPath $msi -Destination (Join-Path $PSScriptRoot "$name.msi") -Force
    Copy-Item -LiteralPath (Join-Path $setup 'UnitManagerSetup.exe') -Destination (Join-Path $PSScriptRoot "$name-Setup.exe") -Force
    @{ version = $version; installer = $true; setup = $true } | ConvertTo-Json -Compress |
        Set-Content -LiteralPath (Join-Path $PSScriptRoot 'latest.json') -Encoding ascii
}
finally {
    # The workspace is a unique directory created solely for this publish.
    if (Test-Path -LiteralPath $work -PathType Container) {
        $target = (Resolve-Path -LiteralPath $work).ProviderPath
        if ($target -eq [IO.Path]::GetFullPath($work)) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }
}
