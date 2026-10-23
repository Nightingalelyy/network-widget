[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $compilerCandidates = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    )
    $compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $compiler) {
        throw 'The Windows .NET Framework C# compiler was not found. Enable or install .NET Framework 4.8, then run Test.ps1 again.'
    }

    $outputDirectory = Join-Path $PSScriptRoot 'test-output'
    $testSuites = @(
        @{
            Name = 'NetworkMonitorTests'
            Sources = @('NetworkMonitor.cs', 'NetworkMonitorTests.cs')
        },
        @{
            Name = 'ProcessNetworkMonitorTests'
            Sources = @('ProcessNetworkMonitor.cs', 'ProcessNetworkMonitorTests.cs')
        },
        @{
            Name = 'ProcessTrafficViewTests'
            Sources = @('ProcessNetworkMonitor.cs', 'SpeedFormatter.cs', 'ProcessTrafficView.cs', 'ProcessTrafficViewTests.cs')
            References = @('System.Drawing.dll', 'System.Windows.Forms.dll')
        }
    )

    # Compile each suite independently so its own Main is the only entry point.
    foreach ($suite in $testSuites) {
        foreach ($sourceName in $suite.Sources) {
            $sourcePath = Join-Path $PSScriptRoot $sourceName
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                throw ('Required test source is missing: {0}' -f $sourcePath)
            }
        }
    }

    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    foreach ($suite in $testSuites) {
        $testExecutable = Join-Path $outputDirectory ($suite.Name + '.exe')
        $compilerArguments = @(
            '/nologo',
            '/utf8output',
            '/optimize+',
            '/target:exe',
            '/platform:anycpu',
            '/langversion:5',
            '/warn:4',
            '/reference:System.dll',
            '/reference:System.Core.dll',
            ('/out:{0}' -f $testExecutable)
        )
        if ($suite.ContainsKey('References')) {
            $compilerArguments += @($suite.References | ForEach-Object { '/reference:' + $_ })
        }
        $compilerArguments += @($suite.Sources | ForEach-Object { Join-Path $PSScriptRoot $_ })
        Write-Host ('Building {0}...' -f $suite.Name)
        & $compiler @compilerArguments
        if ($LASTEXITCODE -ne 0) {
            throw ('Could not compile {0} (exit code {1}). See the compiler messages above.' -f $suite.Name, $LASTEXITCODE)
        }

        Write-Host ('Running {0}...' -f $suite.Name)
        & $testExecutable
        if ($LASTEXITCODE -ne 0) {
            throw ('{0} failed (exit code {1}).' -f $suite.Name, $LASTEXITCODE)
        }
    }
    Write-Host 'All measurement and process view test suites passed.'
}
catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    exit 1
}
