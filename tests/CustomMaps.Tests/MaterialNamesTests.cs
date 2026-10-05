using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The shader-name fallbacks pass over materials copied for one renderer, which a map played
    /// earlier in the session still owns while a custom map is prepared, and which go with it.
    /// </summary>
    public class MaterialNamesTests
    {
        [Theory]
        [InlineData("Ocean (Instance)")]
        [InlineData("Runway_Asphalt (Instance)")]
        [InlineData("Ocean (Instance) (Instance)")]
        public void ACopyForOneRendererIsPassedOver(string name)
        {
            Assert.True(MaterialNames.IsPerRendererCopy(name));
        }

        [Theory]
        [InlineData("Ocean")]
        [InlineData("Runway_Asphalt")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Instance")]
        [InlineData("(Instance)")]
        [InlineData("Ocean (instance)")]
        [InlineData("Ocean (Instance) copy")]
        public void ASharedMaterialIsNot(string name)
        {
            Assert.False(MaterialNames.IsPerRendererCopy(name));
        }
    }
}
