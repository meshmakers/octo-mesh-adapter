param ($configuration = "Release")

# Install/update the API doc generator (provides the 'mmxmldoc2md' command used below).
dotnet tool update --global Meshmakers.XMLDoc2Markdown

$modulePath = Split-Path -Parent $MyInvocation.MyCommand.Definition
$baseBinPath = Join-Path $modulePath "../bin/$configuration/net10.0"
if (-not (Test-Path -Path $baseBinPath)) {
    throw "Bin path '$baseBinPath' does not exist"
}

$baseOutputPath = Join-Path $baseBinPath "documentation"

# Clean directory
if (Test-Path -Path $baseOutputPath) {
    Write-Host "Remove existing documentation at '$baseOutputPath'"
    Remove-Item -Path $baseOutputPath -Recurse -Force
}

# Create XML documentation for Libraries.
# Meshmakers.Octo.MeshAdapter is the host executable and exposes no public API of its own - its
# only public type is the Program class Roslyn generates for top-level statement apps. It is
# therefore not documented here.
$outputPath = "$baseOutputPath/apiReference/Adapters/MeshNodes"
$sourcePath = "$baseBinPath/Meshmakers.Octo.MeshAdapter.Nodes.dll"
Write-Host "Creating documentation for $sourcePath, doc is generated at $outputPath"
mmxmldoc2md $sourcePath $outputPath