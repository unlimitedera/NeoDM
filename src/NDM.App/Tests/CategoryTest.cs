// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System;
using System.IO;
using System.Linq;
using NDM.App;
using NDM.Core;

/// <summary>Verify category-based folder organization saves files to correct subfolders.</summary>
internal static class CategoryTest
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ndm-category-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var baseDir = Path.Combine(root, "downloads");
            var state = Path.Combine(root, "state");

            // Test 1: Category folders disabled - all files go to base directory
            Console.WriteLine("[Category Test] Testing with category folders DISABLED...");
            var settings = new AppSettings
            {
                DownloadDirectory = baseDir,
                EnableCategoryFolders = false
            };

            using (var manager = new DownloadManager(
                baseDir,
                state,
                new DownloadOptions { MaxConcurrentDownloads = 1, MaxSegments = 1, RetryCount = 0 },
                fileName => CategoryHelper.GetDownloadPath(fileName, settings)))
            {
                var job = manager.AddAsync(new DownloadRequest("http://httpbin.org/bytes/1024", "test.exe")).Result;
                if (!Path.GetDirectoryName(job.FilePath)!.Equals(baseDir, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Expected file in base directory, got: {job.FilePath}");
                Console.WriteLine($"  ✓ test.exe → {job.FilePath}");
            }

            // Clean up for next test
            if (Directory.Exists(state)) Directory.Delete(state, true);

            // Test 2: Category folders enabled - files go to category subfolders
            Console.WriteLine("[Category Test] Testing with category folders ENABLED...");
            settings.EnableCategoryFolders = true;

            using (var manager = new DownloadManager(
                baseDir,
                state,
                new DownloadOptions { MaxConcurrentDownloads = 1, MaxSegments = 1, RetryCount = 0 },
                fileName => CategoryHelper.GetDownloadPath(fileName, settings)))
            {
                var tests = new[]
                {
                    ("app.exe", "Programs"),
                    ("setup.msi", "Programs"),
                    ("archive.zip", "Compressed"),
                    ("data.7z", "Compressed"),
                    ("song.mp3", "Music"),
                    ("audio.flac", "Music"),
                    ("movie.mp4", "Video"),
                    ("clip.mkv", "Video"),
                    ("report.pdf", "Documents"),
                    ("sheet.xlsx", "Documents"),
                    ("readme.txt", "Documents"),
                    ("data.bin", null) // General - no category subfolder
                };

                foreach (var (fileName, expectedCategory) in tests)
                {
                    var job = manager.AddAsync(new DownloadRequest("http://httpbin.org/bytes/512", fileName)).Result;
                    var expectedDir = expectedCategory != null
                        ? Path.Combine(baseDir, expectedCategory)
                        : baseDir;
                    var actualDir = Path.GetDirectoryName(job.FilePath)!;

                    if (!actualDir.Equals(expectedDir, StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Expected {fileName} in {expectedDir}, got: {actualDir}");

                    Console.WriteLine($"  ✓ {fileName} → {Path.GetDirectoryName(job.FilePath)}");
                }
            }

            // Clean up for next test
            if (Directory.Exists(state)) Directory.Delete(state, true);

            // Test 3: Custom absolute paths
            Console.WriteLine("[Category Test] Testing with CUSTOM absolute paths...");
            var customProgramsPath = Path.Combine(root, "custom-programs");
            settings.ProgramsFolder = customProgramsPath;

            using (var manager = new DownloadManager(
                baseDir,
                state,
                new DownloadOptions { MaxConcurrentDownloads = 1, MaxSegments = 1, RetryCount = 0 },
                fileName => CategoryHelper.GetDownloadPath(fileName, settings)))
            {
                var job = manager.AddAsync(new DownloadRequest("http://httpbin.org/bytes/256", "installer.exe")).Result;
                var actualDir = Path.GetDirectoryName(job.FilePath)!;
                
                if (!actualDir.Equals(customProgramsPath, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Expected installer.exe in {customProgramsPath}, got: {actualDir}");
                
                Console.WriteLine($"  ✓ installer.exe → {actualDir}");
            }

            // Clean up for next test
            if (Directory.Exists(state)) Directory.Delete(state, true);

            // Test 4: Verify CategoryHelper.GetCategory
            Console.WriteLine("[Category Test] Testing CategoryHelper.GetCategory...");
            var categoryTests = new[]
            {
                ("test.exe", CategoryHelper.Category.Programs),
                ("archive.zip", CategoryHelper.Category.Compressed),
                ("song.mp3", CategoryHelper.Category.Music),
                ("video.mp4", CategoryHelper.Category.Video),
                ("doc.pdf", CategoryHelper.Category.Documents),
                ("unknown.bin", CategoryHelper.Category.General),
                ("NoExtension", CategoryHelper.Category.General)
            };

            foreach (var (fileName, expectedCategory) in categoryTests)
            {
                var actual = CategoryHelper.GetCategory(fileName);
                if (actual != expectedCategory)
                    throw new Exception($"Expected category {expectedCategory} for {fileName}, got {actual}");
                Console.WriteLine($"  ✓ {fileName} → {actual}");
            }

            // Test 5: AppSettings serialization/deserialization
            Console.WriteLine("[Category Test] Testing AppSettings serialization...");
            var settingsDir = Path.Combine(root, "settings-test");
            var testSettings = new AppSettings
            {
                DownloadDirectory = baseDir,
                EnableCategoryFolders = true,
                ProgramsFolder = "MyPrograms",
                CompressedFolder = "Archives",
                MusicFolder = "Audio",
                VideoFolder = "Videos",
                DocumentsFolder = "Docs"
            };
            testSettings.Save(settingsDir);
            var loaded = AppSettings.Load(settingsDir);
            
            if (!loaded.EnableCategoryFolders) throw new Exception("EnableCategoryFolders not persisted");
            if (loaded.ProgramsFolder != "MyPrograms") throw new Exception("ProgramsFolder not persisted");
            if (loaded.CompressedFolder != "Archives") throw new Exception("CompressedFolder not persisted");
            if (loaded.MusicFolder != "Audio") throw new Exception("MusicFolder not persisted");
            if (loaded.VideoFolder != "Videos") throw new Exception("VideoFolder not persisted");
            if (loaded.DocumentsFolder != "Docs") throw new Exception("DocumentsFolder not persisted");
            
            Console.WriteLine("  ✓ All category settings persisted correctly");

            Console.WriteLine("[Category Test] ✓ All tests PASSED");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch { /* Best effort cleanup */ }
        }
    }
}
