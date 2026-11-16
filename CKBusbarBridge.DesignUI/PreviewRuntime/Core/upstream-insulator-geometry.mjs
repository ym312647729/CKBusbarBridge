import {DefinitionError} from './expressions.mjs';
import {baseInsulatorInput} from './insulator-design.mjs';
import {deriveInsulatorAssembly} from './insulator-preview.mjs';
import {evaluatePreview} from './preview-quality.mjs';
import {solidCentroid} from './solid-centroid.mjs';
import {swMatrix,inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';
import {nativeSceneToPreview} from './structure-installation-preview.mjs';
const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'必须唯一，实际'+rows.length);return rows[0];};

export async function readUpstreamInsulatorConnections(host,design,read,kernel){
  if(!kernel)throw new DefinitionError('侧对侧原绝缘子接口需要纯几何内核计算实体中心');
  const connections=[];
  for(const placement of host.insulators){
    const installed=one(design.installation.preview.instances.filter(row=>row.instanceId===placement.design.modelId),'绝缘子组安装位置'),nativePose=multiplyMatrix(inverseRigidMatrix(nativeSceneToPreview),installed.transform),assembly=await deriveInsulatorAssembly(baseInsulatorInput(placement),read),{recipe,sourceBody,childConfiguration,solution}=assembly,top=assembly.root.configurations.find(row=>row.name===recipe.configuration);
    const face=one(sourceBody.configurations.find(row=>row.name===recipe.shared.body.configuration).features.filter(row=>row.name==='配合面外形1'&&row.sketch),'绝缘子配合面'),circle=one(face.sketch.segments.filter(row=>row.type===1&&!row.construction),'绝缘子配合圆'),center=transformPoint(circle.geometry.center.map(value=>value*1000),inverseRigidMatrix(swMatrix(face.sketch.modelToSketch))),normal=face.sketch.support.entity.planeParameters.slice(0,3);
    center[1]*=assembly.heightRatio;
    const bodyResult=await evaluatePreview(kernel,sourceBody,recipe.shared.body.configuration,{}, {arcSegments:32},read);let centroid;
    try{centroid=solidCentroid(bodyResult.solids);centroid[1]*=assembly.heightRatio;}finally{bodyResult.solids.forEach(solid=>solid.delete());}
    const body=one(childConfiguration.components.filter(row=>row.id===recipe.shared.body.componentId),'绝缘子本体'),bodyPose=swMatrix(body.transform),movement=one(solution.translations.filter(row=>row.componentId===body.id),'本体受驱平移');for(let axis=0;axis<3;axis++)bodyPose[12+axis]+=movement.delta[axis];
    for(const group of recipe.groups.filter(row=>!row.suppressed)){
      const groupPose=swMatrix(one(top.components.filter(row=>row.id===group.componentId),'绝缘子相组').transform);groupPose[14]=recipe.layout.groupLocalZ[group.group-1];
      const pose=multiplyMatrix(nativePose,multiplyMatrix(groupPose,bodyPose)),origin=pose.slice(12,15),direction=transformPoint(normal,pose).map((value,index)=>value-origin[index]),length=Math.hypot(...direction);
      connections.push({name:placement.design.modelId+':group:'+group.group,center:transformPoint(center,pose),solidCenter:transformPoint(centroid,pose),normal:direction.map(value=>value/length),radius:circle.geometry.radius*1000,source:{templatePath:sourceBody.source.path,feature:face.name,featureId:face.id,group:group.group,centroid:'constructed closed body volume moments'}});
    }
  }
  return connections;
}
