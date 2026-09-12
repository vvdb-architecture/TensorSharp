using System.Text.Json;
using TensorSharp.GGML;
var reports=new List<object>();
foreach(string directory in args.Take(args.Length-1)) {
 using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"manifest.json")));
 IntPtr handle=GgmlDeepSeek41VisionNative.TSGgml_Dsv41VisionLoad(Path.Combine(directory,"deepseek41.vision.gguf"),"CPU",0,4);
 if(handle==IntPtr.Zero) throw new Exception("VisionLoad failed");
 try {
  int[] info=GgmlDeepSeek41VisionNative.Info(handle);
  foreach(var item in manifest.RootElement.GetProperty("cases").EnumerateArray()) {
   float[] Read(string path){byte[] b=File.ReadAllBytes(Path.Combine(directory,path));float[] f=new float[b.Length/4];Buffer.BlockCopy(b,0,f,0,b.Length);return f;}
   var patches=Read(item.GetProperty("patches").GetString()!);var expected=Read(item.GetProperty("expected").GetString()!);
   int rows=item.GetProperty("patch_grid")[0].GetInt32(),cols=item.GetProperty("patch_grid")[1].GetInt32();var output=new float[expected.Length];
   int n=GgmlDeepSeek41VisionNative.Encode(handle,patches,rows,cols,output);
   double max=0,rms=0; for(int i=0;i<output.Length;i++){double d=output[i]-expected[i];max=Math.Max(max,Math.Abs(d));rms+=d*d;}
   rms=Math.Sqrt(rms/output.Length);
   if(n!=item.GetProperty("tokens").GetInt32() || max>(directory.Contains("bf16") ? 0.004 : 0.000002) || !output.All(float.IsFinite))throw new Exception($"Mismatch {directory}: {max}");
   reports.Add(new{directory,name=item.GetProperty("name").GetString(),n,info,max_absolute_error=max,rms_error=rms,passed=true});
  }
 } finally {GgmlDeepSeek41VisionNative.TSGgml_Dsv41VisionFree(handle);}
}
File.WriteAllText(args[^1],JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"Passed {reports.Count} managed/native vision bridge cases");
