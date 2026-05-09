using System.Reflection;
using System.Runtime.Versioning;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Native.Tests;

public class NativeAssemblyTests
{
    [Fact]
    public void Assembly_LoadsSuccessfully()
    {
        var asm = typeof(NativeMarker).Assembly;
        asm.GetName().Name.Should().Be("CopilotSessionManager.Native");
    }

    [Fact]
    public void Assembly_TargetsWindowsTfm()
    {
        var tfm = typeof(NativeMarker).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?
            .FrameworkName;
        tfm.Should().NotBeNull();
        tfm!.Should().StartWith(".NETCoreApp,Version=v8.0");
    }
}
