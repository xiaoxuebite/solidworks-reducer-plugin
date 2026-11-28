function Read-PluginPath([string]$PointerFile) {
    $path = [IO.File]::ReadAllText($PointerFile).Trim()
    if (![IO.Path]::IsPathRooted($path)) {
        $path = Join-Path (Split-Path -Parent $PointerFile) $path
    }
    return [IO.Path]::GetFullPath($path)
}
