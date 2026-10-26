# SDS Store Visualizer

A local, read-only browser viewer for inspecting SDS store files.

## Run

```powershell
dotnet run --project tools/Applications/SdsStoreVisualizer/SdsStoreVisualizer.csproj
```

Open `http://127.0.0.1:5175` and enter a local namespace folder such as `C:\LibsLocal\TestService`.

The viewer reads files with shared read access and shows a consistency warning because a running SDS service can change files during a scan. It does not start SDS recovery, checkpoint, relink, repair, or mutation code.

## Current decoder scope

- Name-store slots: 512-byte slots with the SDS UTF-8 length prefix.
- Header-store slots: 64-byte slots with a stream-header interpretation and raw bytes.
- Variable blocks: signed 32-bit payload lengths, tombstones, terminators, and 128-byte alignment.
- Object blocks: best-effort key and JSON preview.
- Any unknown or ambiguous file remains available through its raw byte view.

The viewer reports slot size, item size, payload size, allocated block size, page size, and file size separately. It does not assume one universal block size.
