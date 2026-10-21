using MinorShift.Emuera.Files;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0012：序列化 Loader 相关测试类——它们共享进程级 static（GamePaths.Current / Preload.files /
/// GlobalStatic.Process 单赋值 / JSONConfig.Data / Lang），并行会竞态。同 collection 内 xUnit 串行执行。
/// </summary>
[CollectionDefinition("LoaderTests", DisableParallelization = true)]
public class LoaderTestCollection { }
