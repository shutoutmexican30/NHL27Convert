#!/bin/bash
set -e
cd "$(dirname "$0")"
"/c/Program Files/dotnet/dotnet.exe" "C:\Program Files\dotnet\sdk\8.0.423\Roslyn\bincore\csc.dll" -nologo -nullable:enable -langversion:latest -target:exe -out:bin/manual/rinksharp.dll @bin/manual/refs.rsp GlobalUsings.cs Srgb.cs Grade.cs RinkFrame.cs MipChain.cs Bc7Dds.cs Verifier.cs Program.cs
echo OK
