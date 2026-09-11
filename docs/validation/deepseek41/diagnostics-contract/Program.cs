using TensorSharp.GGML;
string actual=GgmlBasicOps.BackendFailureText();
bool expectedFailure=Environment.GetEnvironmentVariable("TS_DIAGNOSTIC_FAILURE_STUB")!=null;
string expected=expectedFailure?"simulated command buffer failure details":string.Empty;
if(actual!=expected)throw new Exception($"Expected [{expected}], got [{actual}]");
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{test="managed diagnostic guard with native ABI stub",failure_latched=expectedFailure,text=actual,passed=true}));
