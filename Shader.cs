using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>Compiles a vertex+fragment shader pair from files and caches uniform locations.</summary>
public class Shader : IDisposable
{
    public int Handle { get; }
    private readonly Dictionary<string, int> _uniformLocations = new();

    public Shader(string vertexPath, string fragmentPath)
    {
        int vert = Compile(ShaderType.VertexShader, vertexPath);
        int frag = Compile(ShaderType.FragmentShader, fragmentPath);

        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, vert);
        GL.AttachShader(Handle, frag);
        GL.LinkProgram(Handle);
        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0)
            throw new Exception($"Shader link error ({vertexPath} + {fragmentPath}):\n{GL.GetProgramInfoLog(Handle)}");

        GL.DetachShader(Handle, vert);
        GL.DetachShader(Handle, frag);
        GL.DeleteShader(vert);
        GL.DeleteShader(frag);

        GL.GetProgram(Handle, GetProgramParameterName.ActiveUniforms, out int uniformCount);
        for (int i = 0; i < uniformCount; i++)
        {
            string name = GL.GetActiveUniform(Handle, i, out _, out _);
            _uniformLocations[name] = GL.GetUniformLocation(Handle, name);
        }
    }

    private static int Compile(ShaderType type, string path)
    {
        string source = File.ReadAllText(path);
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            throw new Exception($"{type} compile error in {path}:\n{GL.GetShaderInfoLog(shader)}");
        return shader;
    }

    public void Use() => GL.UseProgram(Handle);

    private int Loc(string name)
        => _uniformLocations.TryGetValue(name, out int loc) ? loc : -1;

    public void SetMatrix4(string name, Matrix4 value) => GL.UniformMatrix4(Loc(name), false, ref value);
    public void SetVector3(string name, Vector3 value) => GL.Uniform3(Loc(name), value);
    public void SetVector4(string name, Vector4 value) => GL.Uniform4(Loc(name), value);
    public void SetVector2(string name, Vector2 value) => GL.Uniform2(Loc(name), value);
    public void SetFloat(string name, float value)     => GL.Uniform1(Loc(name), value);
    public void SetInt(string name, int value)         => GL.Uniform1(Loc(name), value);

    public void Dispose()
    {
        GL.DeleteProgram(Handle);
        GC.SuppressFinalize(this);
    }
}
