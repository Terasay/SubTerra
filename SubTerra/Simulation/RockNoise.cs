using System.Numerics;

namespace SubTerra.Simulation;

public static class RockNoise
{
    private static float Hash(int x, int y, int z)
    {
        unchecked
        {
            var h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)z * 2147483647u + 1442695041u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xffffff) / 8388607.5f - 1;
        }
    }
    private static float Fade(float t) => t * t * (3 - 2 * t);
    private static float Mix(float a, float b, float t) => a + (b - a) * t;
    public static float Noise(Vector3 p)
    {
        var x = (int)MathF.Floor(p.X); var y = (int)MathF.Floor(p.Y); var z = (int)MathF.Floor(p.Z);
        var u = Fade(p.X - x); var v = Fade(p.Y - y); var w = Fade(p.Z - z);
        return Mix(Mix(Mix(Hash(x,y,z), Hash(x+1,y,z), u), Mix(Hash(x,y+1,z), Hash(x+1,y+1,z), u), v),
            Mix(Mix(Hash(x,y,z+1), Hash(x+1,y,z+1), u), Mix(Hash(x,y+1,z+1), Hash(x+1,y+1,z+1), u), v), w);
    }
    public static float Fractal(Vector3 p, int octaves = 4)
    {
        var sum = 0f; var amplitude = .55f;
        for (var i = 0; i < octaves; i++) { sum += Noise(p) * amplitude; p = p * 2.07f + new Vector3(13.7f); amplitude *= .5f; }
        return sum;
    }
    public static float Tile(float u, float v, int frequency)
    {
        var x = u * frequency; var y = v * frequency;
        var ix = (int)MathF.Floor(x); var iy = (int)MathF.Floor(y);
        int Wrap(int n) => (n % frequency + frequency) % frequency;
        return Mix(Mix(Hash(Wrap(ix), Wrap(iy), 5), Hash(Wrap(ix+1), Wrap(iy), 5), Fade(x-ix)),
            Mix(Hash(Wrap(ix), Wrap(iy+1), 5), Hash(Wrap(ix+1), Wrap(iy+1), 5), Fade(x-ix)), Fade(y-iy));
    }
}
