namespace Xur.Robot;

// OpenCV Brown-Conrady convention: k1,k2,p1,p2,k3. Only supplied calibration
// is used. Fisheye/rational models require another explicitly reviewed adapter.
public static class RobotPoseMath
{
    public static double[] Distort(RobotCameraMetrology camera,double x,double y)
    {
        if(camera.DistortionModel=="none")return [x,y];
        var d=camera.Distortion;var r2=x*x+y*y;var radial=1+d[0]*r2+d[1]*r2*r2+d[4]*r2*r2*r2;
        return [x*radial+2*d[2]*x*y+d[3]*(r2+2*x*x),y*radial+d[2]*(r2+2*y*y)+2*d[3]*x*y];
    }
    public static double[] Undistort(RobotCameraMetrology camera,double[] point)
    {
        if(point is not {Length:2}||point.Any(v=>!double.IsFinite(v)))throw new InvalidOperationException("Invalid pixel point.");
        var dx=(point[0]-camera.Cx)/camera.Fx;var dy=(point[1]-camera.Cy)/camera.Fy;var x=dx;var y=dy;
        if(camera.DistortionModel=="none")return [point[0],point[1]];
        for(var iteration=0;iteration<30;iteration++)
        {
            var predicted=Distort(camera,x,y);var ex=predicted[0]-dx;var ey=predicted[1]-dy;
            if(!double.IsFinite(ex)||!double.IsFinite(ey)||Math.Abs(x)>10||Math.Abs(y)>10)break;
            var coefficients=camera.Distortion;var r2=x*x+y*y;
            var radial=1+coefficients[0]*r2+coefficients[1]*r2*r2+coefficients[4]*r2*r2*r2;
            var slope=2*(coefficients[0]+2*coefficients[1]*r2+3*coefficients[4]*r2*r2);
            var a=radial+x*x*slope+2*coefficients[2]*y+6*coefficients[3]*x;
            var b=x*y*slope+2*coefficients[2]*x+2*coefficients[3]*y;
            var c=b;var d=radial+y*y*slope+6*coefficients[2]*y+2*coefficients[3]*x;var determinant=a*d-b*c;
            // A zero residual at a folded lens root is still invalid. This is
            // a local invertibility guard, not proof of a globally unique lens inverse.
            if(!double.IsFinite(a)||!double.IsFinite(d)||!double.IsFinite(determinant)||a<=1e-12||d<=1e-12||determinant<=1e-12)break;
            if(Math.Max(Math.Abs(ex)*camera.Fx,Math.Abs(ey)*camera.Fy)<1e-6)return [x*camera.Fx+camera.Cx,y*camera.Fy+camera.Cy];
            x-=(d*ex-b*ey)/determinant;y-=(-c*ex+a*ey)/determinant;
        }
        throw new InvalidOperationException("Distortion inversion did not converge; no metric pose is available.");
    }
    public static double[][] ObjectCorners(double edge)=>[[-edge/2,edge/2,0],[edge/2,edge/2,0],[edge/2,-edge/2,0],[-edge/2,-edge/2,0]];
    public static double[] Transform(double[] rotation,double[] translation,double[] point)=>
        [rotation[0]*point[0]+rotation[1]*point[1]+rotation[2]*point[2]+translation[0],
         rotation[3]*point[0]+rotation[4]*point[1]+rotation[5]*point[2]+translation[1],
         rotation[6]*point[0]+rotation[7]*point[1]+rotation[8]*point[2]+translation[2]];
    public static double[] Project(RobotCameraMetrology camera,double[] point)
    {
        if(point[2]<=1e-9)throw new InvalidOperationException("Tag corner has nonpositive depth.");
        var distorted=Distort(camera,point[0]/point[2],point[1]/point[2]);
        var pixel=new[]{camera.Fx*distorted[0]+camera.Cx,camera.Fy*distorted[1]+camera.Cy};
        if(pixel.Any(v=>!double.IsFinite(v)))throw new InvalidOperationException("Nonfinite reprojection.");return pixel;
    }
    public static RobotPoseCandidate Evaluate(RobotCameraMetrology camera,double edge,double[][] pixels,NativeTagPose pose,double limit)
    {
        if(pose==null||pose.Rotation is not {Length:9}||pose.Translation is not {Length:3}
            ||pose.Rotation.Concat(pose.Translation).Any(v=>!double.IsFinite(v))||!double.IsFinite(pose.ObjectSpaceError)||pose.ObjectSpaceError<0)
            throw new InvalidOperationException("Native estimator returned a nonfinite or malformed candidate.");
        var r=pose.Rotation;var problems=new List<string>();
        for(var row=0;row<3;row++)for(var other=row;other<3;other++)
        {
            var dot=Enumerable.Range(0,3).Sum(i=>r[row*3+i]*r[other*3+i]);
            if(Math.Abs(dot-(row==other?1:0))>1e-5)problems.Add("Rotation is not orthonormal.");
        }
        var determinant=r[0]*(r[4]*r[8]-r[5]*r[7])-r[1]*(r[3]*r[8]-r[5]*r[6])+r[2]*(r[3]*r[7]-r[4]*r[6]);
        if(Math.Abs(determinant-1)>1e-5)problems.Add("Rotation has invalid handedness.");
        var points=ObjectCorners(edge).Select(p=>Transform(r,pose.Translation,p)).ToArray();
        var positive=points.All(p=>p[2]>1e-9);double? rms=null;
        if(!positive)problems.Add("Tag corners have nonpositive camera depth.");
        else
        {
            var projected=points.Select(p=>Project(camera,p)).ToArray();
            rms=Math.Sqrt(projected.Select((p,i)=>Math.Pow(p[0]-pixels[i][0],2)+Math.Pow(p[1]-pixels[i][1],2)).Average());
            if(!double.IsFinite(rms.Value))throw new InvalidOperationException("Nonfinite reprojection error.");
            if(rms>limit)problems.Add("Raw-pixel reprojection exceeds the supplied quality limit.");
        }
        return new(r,pose.Translation,pose.ObjectSpaceError,rms,positive,problems.Count==0,problems.Distinct().ToArray());
    }
}
