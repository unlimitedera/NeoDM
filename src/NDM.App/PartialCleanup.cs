// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
namespace NDM.App;

/// <summary>2.0.0: what the Options button "Clean up partial files" uses: how many bytes of unfinished data no download owns, and the cleanup itself.</summary>
public sealed record PartialCleanup(Func<long> Size, Func<(int Folders, long Bytes)> Clean);
