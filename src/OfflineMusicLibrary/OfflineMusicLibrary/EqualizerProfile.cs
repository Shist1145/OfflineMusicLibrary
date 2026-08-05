using System.Collections.Generic;

namespace OfflineMusicLibrary;

public sealed record EqualizerProfile(float Preamp, IReadOnlyList<float> Bands);
