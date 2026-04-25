using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Cysharp.Diagnostics;
using DynamicData;
using Fmod5Sharp;
using Fmod5Sharp.FmodTypes;
using Microsoft.IO;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reactive;
using System.Reactive.Joins;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FenixProFmodAva.ViewModels;

public class MainViewModel : ViewModelBase
{
    public Interaction<string, string> PickAFolder { get; } = new();

    public Interaction<string[], Unit> MsgBoxError { get; } = new();

    public Interaction<string[], Unit> MsgBoxInfo { get; } = new();

    public Interaction<string[], bool> MsgBoxYesNo { get; } = new();

    public Interaction<string, Unit> AddConsoleLine { get; } = new();


    [Reactive]
    public bool IsPathsReadOnly { get; set; } = false;

    [Reactive]
    public string BanksPath { get; set; } = string.Empty;

    [Reactive]
    public string WavsPath { get; set; } = string.Empty;

    [Reactive]
    public string BuildPath { get; set; } = string.Empty;

    public string FsbPath { get; set; } = string.Empty;

    [Reactive]
    public string ProgressText { get; set; } = "Idle";

    [Reactive]
    public int ProgressMaximum { get; set; } = 1;

    [Reactive]
    public int ProgressValue { get; set; } = 0;

    public Subject<string> ConsoleLine { get; } = new();

    public ReactiveCommand<Unit, Unit> PickBanksFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> PickWavsFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> PickBuildFolderCommand { get; }

    public ReactiveCommand<Unit, Unit> ExtractWavsCommand { get; }
    public ReactiveCommand<Unit, Unit> RebuildBanksCommand { get; }

    private RecyclableMemoryStreamManager memoryStreamManager { get; } = new();

    public MainViewModel()
    {
        this.PickBanksFolderCommand = ReactiveCommand.CreateFromTask(PickBanksFolder);
        this.PickWavsFolderCommand = ReactiveCommand.CreateFromTask(PickWavsFolder);
        this.PickBuildFolderCommand = ReactiveCommand.CreateFromTask(PickBuildFolder);

        this.ExtractWavsCommand = ReactiveCommand.CreateFromTask(ExtractWavs);
        this.RebuildBanksCommand = ReactiveCommand.CreateFromTask(RebuildBanks);

        this.ExtractWavsCommand.ThrownExceptions.Subscribe(async ex =>
        {
            await MsgBoxError.Handle(new string[] {
                    "Something Bad Happened!",
                    $"Error: {ex.Message}" }).ToTask();
        });

        this.RebuildBanksCommand.ThrownExceptions.Subscribe(async ex =>
        {
            await MsgBoxError.Handle(new string[] {
                            "Something Bad Happened!",
                            $"Error: {ex.Message}" }).ToTask();
        });

        var curDir = Environment.CurrentDirectory;

        BanksPath = Path.Combine(curDir, "banks");
        WavsPath = Path.Combine(curDir, "wavs");
        BuildPath = Path.Combine(curDir, "build");
        FsbPath = Path.Combine(curDir, "fsb");

        if (Directory.Exists(BanksPath) == false)
            Directory.CreateDirectory(BanksPath);

        if (Directory.Exists(WavsPath) == false)
            Directory.CreateDirectory(WavsPath);

        if (Directory.Exists(BuildPath) == false)
            Directory.CreateDirectory(BuildPath);

        if (Directory.Exists(FsbPath) == false)
            Directory.CreateDirectory(FsbPath);

    }

    async Task PickBanksFolder()
    {
        var path = await PickAFolder.Handle("Select Banks Folder:").ToTask();

        if (path! == string.Empty)
            return;

        BanksPath = path;
    }

    async Task PickWavsFolder()
    {
        var path = await PickAFolder.Handle("Select Folder for extracted Wavs:").ToTask();

        if (path! == string.Empty)
            return;

        WavsPath = path;
    }

    async Task PickBuildFolder()
    {
        var path = await PickAFolder.Handle("Select Folder for rebuilded Banks:").ToTask();

        if (path! == string.Empty)
            return;

        BuildPath = path;
    }

    int Search(byte[] src, byte[] pattern)
    {
        int maxFirstCharSlot = src.Length - pattern.Length + 1;
        for (int i = 0; i < maxFirstCharSlot; i++)
        {
            if (src[i] != pattern[0]) // compare only first byte
                continue;

            // found a match on first byte, now try to match rest of the pattern
            for (int j = pattern.Length - 1; j >= 1; j--)
            {
                if (src[i + j] != pattern[j]) break;
                if (j == 1) return i;
            }
        }
        return -1;
    }

    List<int> SearchAll(byte[] src, byte[] pattern)
    {
        List<int> indices = new List<int>();
        // 边界条件检查
        if (src == null || pattern == null)
            return indices;

        if (pattern.Length == 0)
            return indices;

        if (src.Length < pattern.Length)
            return indices; // 源数组长度小于目标数组，直接返回空列表

        int maxFirstCharSlot = src.Length - pattern.Length + 1;
        for (int i = 0; i < maxFirstCharSlot; i++)
        {
            if (src[i] != pattern[0]) // compare only first byte
                continue;

            // found a match on first byte, now try to match rest of the pattern
            for (int j = pattern.Length - 1; j >= 1; j--)
            {
                if (src[i + j] != pattern[j]) break;
                if (j == 1)
                {
                    indices.Add(i);
                    i = i + pattern.Length;
                    break;
                }
            }
        }
        return indices;
    }

    async ValueTask<List<string>> ExtractFSB(string filename)
    {

        List<string> fsbFiles = new List<string>();
        using var file = File.OpenRead(filename);
        using var memory = memoryStreamManager.GetStream();

        using var reader = new BinaryReader(memory);
        await file.CopyToAsync(memory);
        memory.Position = 0;
        var headerIndex = Search(memory.GetBuffer(), Encoding.ASCII.GetBytes("SNDH"));
        if (headerIndex <= 0)
        {
            return fsbFiles;
        }
        memory.Position = headerIndex + 4;
        var fsbCount = (reader.ReadInt32() - 4) / 8;
        for (int i = 0; i < fsbCount; i++)
        {
            string fsbFileName = Path.Combine(FsbPath, Path.GetFileNameWithoutExtension(filename)) + $"_{i}" + ".fsb";
            if (headerIndex <= 0)
            {
                break;
            }
            try
            {
                File.Delete(fsbFileName);
            }
            catch (Exception)
            {
                // i dont care now
            }
            using var fsbFile = File.OpenWrite(fsbFileName);
            memory.Position = headerIndex + 12 + (i * 8);
            var nextOffset = reader.ReadInt32();
            var fsbSize = reader.ReadInt32() + 8;
            reader.ReadInt32();
            memory.Position = nextOffset;
            byte[] data = new byte[fsbSize];
            await memory.ReadAsync(data,0, fsbSize);
            await fsbFile.WriteAsync(data, 0, fsbSize);

            await file.FlushAsync();
            await memory.FlushAsync();
            await fsbFile.FlushAsync();
            fsbFiles.Add(fsbFileName);
        }
        return fsbFiles;
    }

    async Task ExtractWavs()
    {
        IsPathsReadOnly = true;

        var proceed = await MsgBoxYesNo.Handle(new string[] {
                    "Warning!",
                    "Following operation will overwrite existing WAV files! Do you want to proceed?" }).ToTask();

        if (proceed == false)
        {
            IsPathsReadOnly = false;
            return;
        }

        try
        {
            //var r = await MsgBoxError.Handle(new string[] { "Neco se posralo!", "Tak to ne otot toto se ale nemelo posrat ale posralo se to ajaj!" }).ToTask();
            //var rr = await MsgBoxInfo.Handle(new string[] { "Neco se stalo!", "Neco se stalo atak dale jo." }).ToTask();
            //var res = await MsgBoxYesNo.Handle(new string[] { "Chcete skibidy toilet?!", "Jste si jisti ze kibidi bididis?" }).ToTask();

            if (Directory.Exists(BanksPath) == false)
            {
                await MsgBoxError.Handle(new string[] {
                    "Something Bad Happened!",
                    "Provided Banks Path doesn't exist!" }).ToTask();
                return;
            }

            if (Directory.Exists(WavsPath) == false)
            {
                await MsgBoxError.Handle(new string[] {
                    "Something Bad Happened!",
                    "Provided Wav Path doesn't exist!" }).ToTask();
                return;
            }

            var files = Directory.EnumerateFiles(BanksPath)
                .ToList()
                .Where(x => x.EndsWith(".bank"))
                .ToList();

            if (files.Count() == 0)
            {
                await MsgBoxError.Handle(new string[] {
                    "Something Bad Happened!",
                    "No Sound Banks Were Found!" }).ToTask();
                return;
            }

            foreach (var file in files)
            {
                var bankDirName = Path.GetFileNameWithoutExtension(file);
                ProgressText = $"Processing: {bankDirName}.bank";
                ProgressMaximum = 1;
                ProgressValue = 0;

                // First we get .fsb from .bank
                var fsbList = await ExtractFSB(file);
                foreach (var fsbFile in fsbList)
                {
                    var wavDir = Path.Combine(WavsPath, Path.GetFileNameWithoutExtension(fsbFile));
                    var tempDir = Path.Combine(Path.Combine(Environment.CurrentDirectory, "temp"), Path.GetFileNameWithoutExtension(fsbFile));
                    if (Directory.Exists(wavDir))
                    {
                        Directory.Delete(wavDir, true);
                    }
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }

                    Directory.CreateDirectory(wavDir);
                    Directory.CreateDirectory(tempDir);
                    using var f = File.OpenRead(fsbFile);
                    using var memory = memoryStreamManager.GetStream();

                    await f.CopyToAsync(memory);
                    memory.Position = 0;
                    try
                    {
                        FmodSoundBank bank = FsbLoader.LoadFsbFromByteArray(memory.GetBuffer());
                        var listFileText = new StringBuilder();

                        ProgressMaximum = bank.Samples.Count;
                        ProgressValue = 0;

                        foreach (var sample in bank.Samples)
                        {
                            ProgressValue++;
                            if (sample == null)
                                continue;

                            if (sample.RebuildAsStandardFileFormat(out var dataBytes, out var fileExtension))
                            {
                                var filename = sample.Name + ".wav";
                                listFileText.AppendLine(filename);
                                var sampleFileName = Path.Combine(wavDir, filename);

                                if (fileExtension.ToLower() != "wav")
                                {
                                    var tempname = sample.Name + "." + fileExtension;
                                    var tempFileName = Path.Combine(tempDir, tempname);
                                    using (var tempFile = File.OpenWrite(tempFileName))
                                    {
                                        tempFile.Write(dataBytes);
                                    }

                                    await AudioConverter.ConvertToWavAsync(tempFileName, sampleFileName);
                                }
                                else
                                {
                                    using var sampleFile = File.OpenWrite(sampleFileName);
                                    FixWav(ref dataBytes);
                                    sampleFile.Write(dataBytes);
                                }
                            }

                            //await Task.Delay(100);
                        }

                        var lstFilename = Path.Combine(wavDir, "files.lst");
                        try
                        {
                            File.Delete(lstFilename);
                        }
                        catch (Exception)
                        {
                        }

                        using var lstFile = File.OpenWrite(lstFilename);
                        using var lstFileWriter = new StreamWriter(lstFile);

                        await lstFileWriter.WriteAsync(listFileText.ToString());
                    }
                    catch (Exception ex)
                    {
                        await MsgBoxError.Handle(new string[] {
                    "Something Bad Happened!",
                    $"bank: {bankDirName},Error: {ex.Message}" }).ToTask();
                        return;
                    }
                }
                
            }
            GC.Collect();
        }
        finally
        {
            IsPathsReadOnly = false;
            ProgressText = "Idle";
            ProgressMaximum = 1;
            ProgressValue = 1;
        }
    }

    private static void FixWav(ref byte[] wav)
    {
        int origLen = wav.Length;
        for (int i = 36; i < origLen - 2; i++)
            wav[i] = wav[i + 2];
        Array.Resize(ref wav, origLen - 2);

        WriteLE32(wav, 4, wav.Length - 8);
        WriteLE32(wav, 16, 16);
        WriteLE32(wav, 40, wav.Length - 44);
    }

    private static void WriteLE32(byte[] buf, int offset, int value)
    {
        byte[] b = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian) Array.Reverse(b);
        b.CopyTo(buf, offset);
    }

    async Task RebuildBanks()
    {
        IsPathsReadOnly = true;

        var proceed = await MsgBoxYesNo.Handle(new string[] {
                    "Warning!",
                    "Following operation will overwrite existing rebuild BANK files! Do you want to proceed?" }).ToTask();

        if (proceed == false)
        {
            IsPathsReadOnly = false;
            return;
        }

        try
        {
            if (Directory.Exists(BanksPath) == false)
            {
                await MsgBoxError.Handle(new string[] {
                    "Something Bad Happened!",
                    "Provided banks Path doesn't exist!" }).ToTask();
                return;
            }
            if (Directory.Exists(WavsPath) == false)
            {
                await MsgBoxError.Handle(new string[] {
                    "Something Bad Happened!",
                    "Provided Wav Path doesn't exist!" }).ToTask();
                return;
            }


            var banks= Directory.EnumerateFiles(BanksPath).ToList();
            ProgressMaximum = banks.Count;
            ProgressValue = 0;
            foreach (var bank in banks)
            {
                try
                {
                    var bankName = Path.GetFileNameWithoutExtension(bank);

                    ProgressText = $"Building {bankName}.bank";
                    ProgressValue++;

                    if (bankName == null)
                    {
                        throw new Exception("Bank Name was null!");
                    }
                    var wavDirs = Directory.EnumerateDirectories(WavsPath).Where(x =>
                    {
                        var exist = File.Exists(Path.Combine(x, "files.lst"));
                        return exist && Regex.IsMatch(Path.GetFileNameWithoutExtension(x), $"^{bankName}_\\d+$");
                    }).ToList();

                    if (wavDirs.Count == 0)
                    {
                        throw new Exception("wav is empty!");
                    }
                    List<string> fsbFiles = new List<string>();
                    for (int i = 0; i < wavDirs.Count; i++)
                    {
                        var wavDir = wavDirs.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s) == $"{bankName}_{i}");
                        if (wavDir == null)
                        {
                            break;
                        }
                        var fsbFile = Path.Combine(FsbPath, Path.GetFileNameWithoutExtension(wavDir)) + ".fsb";
                        var lstFile = Path.Combine(wavDir, "files.lst");
                        var threadCount = Environment.ProcessorCount;
                        // async iterate.
                        await foreach (string item in ProcessX.StartAsync(
                            $"FMOD\\fsbankcl.exe -rebuild -thread_count {threadCount} -format Vorbis -ignore_errors -quality 85 -verbosity 5 -o {fsbFile} {lstFile}"))
                        {
                            await AddConsoleLine.Handle(item).ToTask();
                        }
                        fsbFiles.Add(fsbFile);
                    }
                    if (fsbFiles.Count != wavDirs.Count)
                    {
                        continue;
                    }

                    var originalBankPath = Path.Combine(BanksPath, bankName) + ".bank";
                    using var originalBank = File.OpenRead(originalBankPath);
                    using var originalMemory = memoryStreamManager.GetStream();

                    await originalBank.CopyToAsync(originalMemory);

                    using var orignalBankReader = new BinaryReader(originalMemory);

                    originalMemory.Position = 0;

                    var headerIndex = Search(originalMemory.GetBuffer(), Encoding.ASCII.GetBytes("SNDH"));
                    if (headerIndex <= 0)
                    {
                        throw new Exception("bank is bad!");
                    }
                    originalMemory.Position = headerIndex + 4;
                    var fsbCount = (orignalBankReader.ReadInt32() - 4) / 8;
                    if (fsbCount != fsbFiles.Count)
                    {
                        throw new Exception("The number of WAV files does not match the bank.");
                    }
                    originalMemory.Position = headerIndex + 12;
                    var headerSize = orignalBankReader.ReadInt32();
                    var modifiedBankPath = Path.Combine(BuildPath, bankName) + ".bank";

                    try
                    {
                        File.Delete(modifiedBankPath);
                    }
                    catch (Exception)
                    {

                    }

                    using var modifiedBankFile = File.OpenWrite(modifiedBankPath);
                    using var modifiedBankWriter = new BinaryWriter(modifiedBankFile);

                    // copy original header to our new bank
                    originalMemory.Position = 0;
                    await modifiedBankFile.WriteAsync(originalMemory.GetBuffer(), 0, headerSize);
                    var endOffset = (int)modifiedBankFile.Position;
                    int fsbFileTotalSize = 0;
                    int index = 0;
                    foreach (var fsbFile in fsbFiles)
                    {
                        var fsbFileSize = (int)new FileInfo(fsbFile).Length;
                        fsbFileTotalSize += fsbFileSize;
                        // Write new total file size
                        modifiedBankWriter.Seek(headerIndex + 12 + (index * 8), SeekOrigin.Begin);
                        // write new offset for new FSB file
                        modifiedBankWriter.Write(endOffset);
                        modifiedBankWriter.Write(fsbFileSize - 8);
                        modifiedBankWriter.Seek(endOffset, SeekOrigin.Begin);

                        using var modifiedFsbFile = File.OpenRead(fsbFile);

                        //write the FSB content
                        await modifiedFsbFile.CopyToAsync(modifiedBankFile);
                        endOffset = (int)modifiedBankFile.Position;
                        index++;
                    }
                    modifiedBankWriter.Seek(4, SeekOrigin.Begin);
                    modifiedBankWriter.Write((int)(headerSize + fsbFileTotalSize - 8));
                }
                catch (Exception ex)
                {
                    await MsgBoxError.Handle(new string[] {
                    "Something Bad Happened!",
                    ex.Message}).ToTask();
                    continue;
                }
            }
        }
        finally
        {
            IsPathsReadOnly = false;
            ProgressText = "Idle";
            ProgressMaximum = 1;
            ProgressValue = 1;
        }
    }
}
