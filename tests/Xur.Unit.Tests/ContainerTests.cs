using Xur.Domain;
using Xur.Agent;
public static class ContainerTests
{
 public static void Run(Action<bool,string> check)
 {
  var recipe=new Recipe("container-test","Container","sha256:"+new string('a',64),[],8080,"/","CPU",0,0,"",Kind:"Container",Engine:"Podman",Container:new([new("xur-volume-"+new string('b',32),"/data")],new()));
  ProfilePolicy.ValidateRecipe(recipe);var w=new Workload("1","Container",recipe,[],"container-1");
  check(w.Fingerprint==(w with{Name="Renamed",Route="new-route"}).Fingerprint,"Container names and routes preserve runtime fingerprint");
  check(w.Fingerprint!=(w with{Recipe=recipe with{Container=recipe.Container! with{Mounts=[new("xur-volume-"+new string('c',32),"/data")]}}}).Fingerprint,"Changing container volume changes only its fingerprint");
  foreach(var path in new[]{"/../etc","/proc/1","/dev/dri","/sys","relative"})
  {bool failed=false;try{ProfilePolicy.ValidateRecipe(recipe with{Container=new([new("xur-volume-"+new string('b',32),path)],new())});}catch(InvalidOperationException){failed=true;}check(failed,"Container rejects invalid mount destination "+path);}
  check(ModelCatalog.NormalizeQuery("https://huggingface.co/lued/Qwen3.8-27B-INT8-W8A16-MTP/tree/main")=="lued/Qwen3.8-27B-INT8-W8A16-MTP","Hugging Face model URL becomes exact repository ID");
 }
}
