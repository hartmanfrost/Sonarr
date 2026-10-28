using System.Collections.Generic;

namespace NzbDrone.Core.DataAugmentation.Scene
{
    public interface ISceneMappingProvider
    {
        List<SceneMapping> GetSceneMappings();
    }

    /// <summary>
    /// A provider that only adds names. It runs after the others, and a mapping whose name is already claimed by
    /// a different series in another provider's rows is dropped, because one name for two series makes
    /// FindSceneMapping throw for the title that matched fine before.
    /// </summary>
    public interface ISupplementarySceneMappingProvider : ISceneMappingProvider
    {
    }
}
