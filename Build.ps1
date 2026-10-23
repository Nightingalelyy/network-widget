[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $projectDirectory = $PSScriptRoot
    $resolvedOutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    $outputExecutable = Join-Path $resolvedOutputDirectory 'NetSpeedWidget.exe'

    $compilerCandidates = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    )
    $compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $compiler) {
        throw 'The Windows .NET Framework C# compiler was not found. Enable or install .NET Framework 4.8, then run Build.ps1 again. A separate .NET SDK is not required.'
    }

    # Keep the list explicit: test programs must never be included in the app.
    $sourceNames = @(
        'Program.cs',
        'WidgetForm.cs',
        'Settings.cs',
        'SpeedFormatter.cs',
        'NetworkMonitor.cs',
        'ProcessNetworkMonitor.cs',
        'ProcessTrafficView.cs'
    )
    $sourcePaths = @($sourceNames | ForEach-Object { Join-Path $projectDirectory $_ })
    $manifestPath = Join-Path $projectDirectory 'app.manifest'
    $configurationPath = Join-Path $projectDirectory 'NetSpeedWidget.exe.config'
    $readmePath = Join-Path $projectDirectory 'README.txt'
    $iconPath = Join-Path $projectDirectory 'NetSpeedWidget.ico'

    foreach ($requiredPath in @($sourcePaths) + @($manifestPath, $configurationPath, $readmePath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw ('Required project file is missing: {0}' -f $requiredPath)
        }
    }

    New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
    $compilerArguments = @(
        '/nologo',
        '/utf8output',
        '/target:winexe',
        '/platform:anycpu',
        '/optimize+',
        '/warn:4',
        '/langversion:5',
        '/reference:System.dll',
        '/reference:System.Core.dll',
        '/reference:System.Drawing.dll',
        '/reference:System.Windows.Forms.dll',
        '/reference:System.Xml.dll',
        ('/win32manifest:{0}' -f $manifestPath),
        ('/out:{0}' -f $outputExecutable)
    )
    if (Test-Path -LiteralPath $iconPath -PathType Leaf) {
        $compilerArguments += ('/win32icon:{0}' -f $iconPath)
    }
    $compilerArguments += $sourcePaths

    Write-Host 'Building NetSpeedWidget...'
    & $compiler @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw ('Compilation failed (exit code {0}). See the compiler messages above. If the widget is open, exit it from its menu and try again.' -f $LASTEXITCODE)
    }
    if (-not (Test-Path -LiteralPath $outputExecutable -PathType Leaf)) {
        throw 'The compiler completed without producing NetSpeedWidget.exe.'
    }

    Copy-Item -LiteralPath $configurationPath -Destination (Join-Path $resolvedOutputDirectory 'NetSpeedWidget.exe.config') -Force
    Copy-Item -LiteralPath $readmePath -Destination (Join-Path $resolvedOutputDirectory 'README.txt') -Force
    Write-Host ('Build complete: {0}' -f $outputExecutable)
    Write-Host 'Open NetSpeedWidget.exe to start the widget. Keep its .exe.config file beside it.'
}
catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    exit 1
}
