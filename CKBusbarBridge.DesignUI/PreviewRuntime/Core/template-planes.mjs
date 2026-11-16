import {DefinitionError} from './expressions.mjs';
import {activeTemplateGeometry,listParameters,sketchLoops,swMatrix,inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';
import {linearSketchDrive} from './linear-sketch-drive.mjs';
import {validateInstallationMatrix} from './installation-pose.mjs';

const identity=()=>[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
export const transformDirection=(direction,matrix)=>[0,1,2].map(row=>matrix[row]*direction[0]+matrix[row+4]*direction[1]+matrix[row+8]*direction[2]);
const area=loop=>loop.reduce((sum,point,index)=>{const next=loop[(index+1)%loop.length];return sum+point[0]*next[1]-next[0]*point[1];},0)/2;
function contains(loop,point){let inside=false;for(let index=0,previous=loop.length-1;index<loop.length;previous=index++){const a=loop[index],b=loop[previous];if((a[1]>point[1])!==(b[1]>point[1])&&point[0]<(b[0]-a[0])*(point[1]-a[1])/(b[1]-a[1])+a[0])inside=!inside;}return inside;}

// Analytic planes come from source feature/profile definitions, never from tessellated facets.
// Partial extraction is unusable as a roof: an omitted lower face could change the result.
export async function collectTemplatePlanes(template,configurationName,loadTemplate,overrides={}){
  const planes=[],diagnostics=[];
  async function visit(definition,name,placement,chain,ancestors,values){
    const sourceKey=definition.source.path+'\0'+name;
    if(ancestors.includes(sourceKey))throw new DefinitionError('屋面模板引用形成循环');
    const configuration=definition.configurations.find(row=>row.name===name);
    if(!configuration)throw new DefinitionError('屋面源配置不存在：'+name);
    validateInstallationMatrix(placement);
    if(configuration.components?.length){
      if(activeTemplateGeometry(configuration).length)throw new DefinitionError('屋面装配级几何特征尚未求解');
      if(Object.keys(values).length)throw new DefinitionError('屋面装配尺寸修改后的配合尚未求解');
      for(const component of configuration.components){
        if(component.suppression===0||component.representation?.mode==='deferred')continue;
        const child=component.isVirtual?{...component.embeddedDefinition,source:{...component.embeddedDefinition?.source,path:definition.source.path+'#embedded/'+component.id}}:await loadTemplate(component.path);
        await visit(child,component.configuration,multiplyMatrix(placement,swMatrix(component.transform)),[...chain,{id:component.id,name:component.name,path:component.path,configuration:component.configuration}],[...ancestors,sourceKey],{});
      }
      return;
    }
    const geometry=activeTemplateGeometry(configuration);
    if(geometry.length!==1||!['Extrusion','Boss','SMBaseFlange'].includes(geometry[0].type))throw new DefinitionError('屋面真实平面尚不支持该特征组合');
    if(configuration.equations.some(row=>!row.disabled&&row.userDisposition?.mode!=='void-by-user'))throw new DefinitionError('屋面方程驱动的平面尚未求解');
    const feature=geometry[0],properties=feature.definition?.properties;
    if(!properties||properties.Contours?.length)throw new DefinitionError('屋面缺少完整拉伸轮廓定义');
    const parameters=listParameters(definition,name);
    for(const [parameterName,value]of Object.entries(values))if(!parameters.some(row=>row.name===parameterName&&row.editable)||!Number.isFinite(value)||value<=0)throw new DefinitionError('屋面尺寸没有可验证的驱动定义：'+parameterName);
    const profiles=configuration.features.filter(row=>row.sketch&&feature.parents.includes(row.name));
    if(profiles.length!==1)throw new DefinitionError('屋面没有唯一源草图');
    const drive=linearSketchDrive(configuration,feature),sketch=drive?drive.solve(values).sketch:profiles[0].sketch;
    if(sketch.segments.some(row=>!row.construction&&row.type!==0))throw new DefinitionError('曲线轮廓屋面的解析平面尚未迁入');
    const loops=sketchLoops(sketch),matrix=multiplyMatrix(placement,inverseRigidMatrix(swMatrix(sketch.modelToSketch)));validateInstallationMatrix(matrix);
    let depth,offset;
    if(feature.type==='SMBaseFlange'){
      depth=properties.Thickness*1000;offset=properties.SymmetricThickness?-depth/2:properties.ReverseThickness?0:-depth;
    }else{
      if(properties.isThin||properties.FlipSideToCut||properties.forwardDraftEnabled||properties.reverseDraftEnabled||properties.directionReferences?.some(row=>row.reference)||properties.FromType!==0||![0,6].includes(properties.forwardEndCondition)||properties.BothDirections&&properties.forwardEndCondition!==6)throw new DefinitionError('屋面拉伸方向或终止条件尚未支持');
      const depthParameter=parameters.find(row=>row.editable&&row.name.endsWith('@'+feature.name));
      depth=depthParameter&&Object.hasOwn(values,depthParameter.name)?values[depthParameter.name]:properties.forwardDepth*1000;
      offset=properties.forwardEndCondition===6?-depth/2:properties.ReverseDirection?-depth:0;
    }
    if(!Number.isFinite(depth)||depth<=0)throw new DefinitionError('屋面拉伸深度无效');
    const emit=(role,point,normal)=>planes.push({key:JSON.stringify([chain.map(row=>[row.id,row.name]),feature.id,role]),featureId:feature.id,featureName:feature.name,ownerPath:definition.source.path,configuration:name,componentChain:structuredClone(chain),role,point:transformPoint(point,matrix),normal:transformDirection(normal,matrix),source:'analytic-source-extrusion',units:'mm'});
    emit('start',[...loops[0][0],offset],[0,0,-1]);emit('end',[...loops[0][0],offset+depth],[0,0,1]);
    for(const [loopIndex,loop]of loops.entries()){
      const signedArea=area(loop);if(Math.abs(signedArea)<1e-8)throw new DefinitionError('屋面轮廓面积退化');
      const nesting=loops.filter((other,index)=>index!==loopIndex&&contains(other,loop[0])).length,sign=Math.sign(signedArea)*(nesting%2?-1:1);
      for(const [edgeIndex,point]of loop.entries()){
        const next=loop[(edgeIndex+1)%loop.length],dx=next[0]-point[0],dy=next[1]-point[1],length=Math.hypot(dx,dy);
        if(length<1e-7)throw new DefinitionError('屋面轮廓存在退化直边');
        emit('side/'+loopIndex+'/'+edgeIndex,[...point,offset],[sign*dy/length,-sign*dx/length,0]);
      }
    }
  }
  try{await visit(template,configurationName,identity(),[],[],overrides);if(!planes.length)throw new DefinitionError('屋面没有可用实际平面');}
  catch(error){diagnostics.push({path:template.source.path,configuration:configurationName,message:error.message});return {complete:false,planes:[],diagnostics};}
  return {complete:true,planes,diagnostics};
}
