using Xunit;
using EraCore.Hosting;

namespace EraCore.Hosting.Tests;

public sealed class AlignGeometryTests
{
    [Theory]
    [InlineData("left", 10, 30, 0)]
    [InlineData("center", 10, 30, 10)]
    [InlineData("right", 10, 30, 20)]
    [InlineData(null, 10, 30, 0)]
    [InlineData("center", 40, 30, 0)]
    public void Column_offset_matches_alignment(string? align, int content, int container, int expected)
        => Assert.Equal(expected, AlignGeometry.OffsetColumns(align, content, container));

    [Theory]
    [InlineData("center", 100.0, 300.0, 100.0)]
    [InlineData("right", 100.0, 300.0, 200.0)]
    [InlineData("left", 100.0, 300.0, 0.0)]
    [InlineData("center", 400.0, 300.0, 0.0)]
    public void Pixel_offset_matches_alignment(string? align, double content, double container, double expected)
        => Assert.Equal(expected, AlignGeometry.OffsetPixels(align, content, container));
}
