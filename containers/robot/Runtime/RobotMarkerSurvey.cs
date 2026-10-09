using System.Text.Json;
using System.Text.RegularExpressions;

namespace Xur.Robot;

// Validate the private adapter's observations before persisting public evidence.
// This observer cannot create calibration receipts or arm a robot.
public static class RobotMarkerSurvey
{
    public record AdapterFrame(DateTimeOffset CapturedAt,int Width,int Height,RobotMarkerDetection[] Detections);
    public record AdapterCamera(string Name,AdapterFrame[] Frames,string Jpeg);
    public record AdapterResult(string Family,string DetectorSha256,AdapterCamera[] Cameras);


    public static (RobotMarkerReport Report,Dictionary<string,byte[]> Images) Read(JsonElement result)
    {
        var data=result.Deserialize(RobotJson.Default.AdapterResult)??throw new InvalidOperationException("Marker survey returned no observations.");
        if(data.Family!="tagStandard41h12" || data.DetectorSha256==null || !Regex.IsMatch(data.DetectorSha256,@"\A[a-f0-9]{64}\z")
            || data.Cameras is not {Length:2} || !data.Cameras.Select(c=>c?.Name).Order().SequenceEqual(new[]{"hand","head"}))
            throw new InvalidOperationException("Marker survey must identify its detector and both selected cameras.");
        var cameras=new List<RobotMarkerCamera>();var images=new Dictionary<string,byte[]>();
        foreach(var camera in data.Cameras)
        {
            if(camera.Frames is not {Length:3})throw new InvalidOperationException("Marker survey requires three observations from each camera.");
            var frames=new List<RobotMarkerFrame>();
            foreach(var frame in camera.Frames)
            {
                if(frame==null || frame.Width is <16 or >3840 || frame.Height is <16 or >2160 || frame.CapturedAt==default
                    || frame.Detections==null || frame.Detections.Length>128
                    || frames.Count>0 && (frame.CapturedAt<=frames[^1].CapturedAt || frame.Width!=frames[0].Width || frame.Height!=frames[0].Height))
                    throw new InvalidOperationException("Marker survey returned invalid or unordered camera frames.");
                foreach(var marker in frame.Detections)Validate(marker,frame.Width,frame.Height);
                var duplicateIds=frame.Detections.GroupBy(m=>m.Id).Where(g=>g.Count()>1).Select(g=>g.Key).Order().ToArray();
                frames.Add(new(frame.CapturedAt,frame.Width,frame.Height,frame.Detections,duplicateIds));
            }
            var usable=frames.SelectMany(f=>f.Detections.Where(m=>!f.AmbiguousDuplicateIds.Contains(m.Id))).GroupBy(m=>m.Id);
            var markers=usable.OrderBy(g=>g.Key).Select(group=>
            {
                var samples=group.ToArray();var edges=samples.SelectMany(m=>Enumerable.Range(0,4).Select(i=>Distance(m.Corners[i],m.Corners[(i+1)%4]))).ToArray();
                var mean=new[]{samples.Average(m=>m.Center[0]),samples.Average(m=>m.Center[1])};
                return new RobotMarkerVisibility(group.Key,samples.Length,edges.Min(),edges.Max(),samples.Max(m=>Distance(m.Center,mean)));
            }).ToArray();
            cameras.Add(new(camera.Name,frames.ToArray(),markers));
            byte[] jpeg;
            try{jpeg=Convert.FromBase64String(camera.Jpeg);}
            catch(Exception error) when(error is ArgumentException or FormatException){throw new InvalidOperationException("Marker survey returned an invalid camera image.");}
            if(jpeg.Length is <4 or >2*1024*1024 || jpeg[0]!=0xff || jpeg[1]!=0xd8 || jpeg[^2]!=0xff || jpeg[^1]!=0xd9)
                throw new InvalidOperationException("Marker survey returned an invalid or oversized JPEG.");
            images.Add(camera.Name,jpeg);
        }
        // A shared reference must be unambiguous in every observation of both
        // cameras. Seeing an ID in two asynchronous views is not stereo pose.
        var shared=cameras[0].Markers.Where(m=>m.DetectedFrames==3).Select(m=>m.Id)
            .Intersect(cameras[1].Markers.Where(m=>m.DetectedFrames==3).Select(m=>m.Id)).Order().ToArray();
        return (new(data.Family,cameras.SelectMany(c=>c.Frames).Max(f=>f.CapturedAt),data.DetectorSha256,cameras.ToArray(),shared),images);
    }
    static double Distance(double[] a,double[] b)=>Math.Sqrt(Math.Pow(a[0]-b[0],2)+Math.Pow(a[1]-b[1],2));
    static void Validate(RobotMarkerDetection marker,int width,int height)
    {
        bool Point(double[]? p)=>p is {Length:2} && double.IsFinite(p[0]) && double.IsFinite(p[1])
            && p[0]>=0 && p[0]<width && p[1]>=0 && p[1]<height;
        if(marker==null || marker.Id is <0 or >=2115 || marker.Hamming!=0 || !double.IsFinite(marker.DecisionMargin)
            || marker.DecisionMargin<=0 || !Point(marker.Center) || marker.Corners is not {Length:4} || !marker.Corners.All(Point))
            throw new InvalidOperationException("Marker survey returned an invalid detection.");
        var turns=Enumerable.Range(0,4).Select(i=>
        {
            var a=marker.Corners[i];var b=marker.Corners[(i+1)%4];var c=marker.Corners[(i+2)%4];
            return (b[0]-a[0])*(c[1]-b[1])-(b[1]-a[1])*(c[0]-b[0]);
        }).ToArray();
        if(!(turns.All(t=>t>0)||turns.All(t=>t<0)) || Enumerable.Range(0,4).Any(i=>Distance(marker.Corners[i],marker.Corners[(i+1)%4])<1))
            throw new InvalidOperationException("Marker survey returned degenerate corner geometry.");
    }
}
