using System.Text.Json;
var results=new List<string>();
void Check(bool value,string name){if(!value)throw new Exception(name);results.Add(name);}
if(args is ["--live-markers",var input,var output])await RoboticsTests.LiveMarkers(input,output,Check);
else{EmoteFileTests.Run(Check);await HandCalibrationTests.Run(Check);await CalibrationObservationTests.Run(Check);await RoboticsTests.Run(Check);await MetrologyTests.Run(Check);JointEncoderOffsetTests.Run(Check);await CameraCapabilityTests.Run(Check);await MotorDiagnosticsTests.Run(Check);}
Console.WriteLine(JsonSerializer.Serialize(new{suite="RobotContainerRuntime",passed=results}));
