$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$proj = Join-Path $PSScriptRoot "generate-hope-ico\GenerateHopeIco.csproj"
$full = Join-Path $root "src\resources\hope.png"
$mini = Join-Path $root "src\resources\hope-mini.png"
$ico = Join-Path $root "src\resources\hope.ico"
dotnet run --project $proj -c Release -- $full $mini $ico
