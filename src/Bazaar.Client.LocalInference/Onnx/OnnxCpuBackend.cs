using System.Diagnostics;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Bazaar.Client.LocalInference.Onnx;

/// <summary>
/// ONNX Runtime session on the CPU execution provider for one model file (doc 31, Selection chain: CPU step).
/// </summary>
/// <remarks>
/// The text path is a placeholder until the real tokenizer lands. The request text is encoded as UTF-8 bytes, one float
/// per byte, padded or truncated to the first input's element count. The first output is read back as bytes and decoded.
/// This is a real ORT run, but it only gives meaningful text for identity-like models.
/// </remarks>
public sealed class OnnxCpuBackend : ILocalModelBackend
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int[] _inputShape;
    private readonly int _inputLength;

    /// <summary>Loads the model at <paramref name="modelPath"/>. Throws <see cref="FileNotFoundException"/> when it is missing.</summary>
    public OnnxCpuBackend(string modelPath)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("The ONNX model file is missing.", modelPath);
        }

        using var options = new SessionOptions();
        _session = new InferenceSession(modelPath, options);

        var input = _session.InputMetadata.First();
        var output = _session.OutputMetadata.First();
        if (input.Value.ElementDataType != TensorElementType.Float || output.Value.ElementDataType != TensorElementType.Float)
        {
            _session.Dispose();
            throw new InvalidOperationException("The placeholder text path needs a float input and a float output.");
        }

        _inputName = input.Key;
        _outputName = output.Key;
        _inputShape = input.Value.Dimensions.Select(dimension => dimension > 0 ? dimension : 1).ToArray();
        _inputLength = _inputShape.Aggregate(1, (product, dimension) => product * dimension);
    }

    /// <inheritdoc />
    public BackendTier Tier => BackendTier.Cpu;

    /// <inheritdoc />
    public async Task<LocalTaskResult> RunAsync(LocalTaskRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var input = Encode(request.Input);

        var stopwatch = Stopwatch.StartNew();
        var text = await Task.Run(() => Infer(input, request.MaxOutputTokens), ct);
        stopwatch.Stop();

        return new LocalTaskResult(text, Tier, Degraded: false, stopwatch.Elapsed);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _session.Dispose();
        return ValueTask.CompletedTask;
    }

    private float[] Encode(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var values = new float[_inputLength];
        for (var i = 0; i < Math.Min(bytes.Length, values.Length); i++)
        {
            values[i] = bytes[i];
        }

        return values;
    }

    private string Infer(float[] input, int maxOutputTokens)
    {
        var tensor = new DenseTensor<float>(input, _inputShape);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };

        using var results = _session.Run(inputs);
        var output = results.First(result => result.Name == _outputName).AsTensor<float>().ToArray();

        var bytes = new byte[Math.Min(output.Length, maxOutputTokens)];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)Math.Clamp(MathF.Round(output[i]), 0f, 255f);
        }

        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes);
    }
}
