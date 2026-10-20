param([string]$OutPath, [int]$Width = 480, [int]$Height = 320)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Graphics.Imaging.BitmapEncoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]

$asTaskOp = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
$asTaskAction = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
function Await($op, [Type]$type) { $t = $asTaskOp.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(); $t.Result }
function AwaitAction($op) { $t = $asTaskAction.Invoke($null, @($op)); $t.Wait() }

$stream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
$encoder = Await ([Windows.Graphics.Imaging.BitmapEncoder]::CreateAsync([Windows.Graphics.Imaging.BitmapEncoder]::HeifEncoderId, $stream)) ([Windows.Graphics.Imaging.BitmapEncoder])

$stride = $Width * 4
$px = New-Object byte[] ($stride * $Height)
for ($y = 0; $y -lt $Height; $y++) {
    for ($x = 0; $x -lt $Width; $x++) {
        $i = $y * $stride + $x * 4
        $px[$i] = [byte](($x * 255) / $Width)
        $px[$i + 1] = [byte](($y * 255) / $Height)
        $px[$i + 2] = 200
        $px[$i + 3] = 255
    }
}
$encoder.SetPixelData([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8, [Windows.Graphics.Imaging.BitmapAlphaMode]::Ignore,
    [uint32]$Width, [uint32]$Height, 96.0, 96.0, $px)
AwaitAction ($encoder.FlushAsync())

$net = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($stream.GetInputStreamAt(0))
$fs = [IO.File]::Create($OutPath)
$net.CopyTo($fs)
$fs.Close()
Get-Item $OutPath | Select-Object Name, Length
