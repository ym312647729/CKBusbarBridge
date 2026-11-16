import {DefinitionError,solveEquations} from './expressions.mjs';
import {capturedDimensionValues} from './equation-context.mjs';
import {swMatrix,inverseRigidMatrix,transformPoint} from './geometry.mjs';
const near=(first,second)=>Math.hypot(...first.map((value,axis)=>value-second[axis]))<1e-5;

// The original 024 guides place the two ends at +/-L/2 and H/2. Support
// positions are measured from -L/2; their mounting surface is 41.5 above datum.
// These equations apply only to the named original template and are checked
// against all captured source coordinate origins before producing a new pose.
export function driveRegularFrameCoordinates(source,updated,model){
  if(model.templatePath!=='模板/基架模板/常规基架/5GX.355.024.SLDASM')return false;
  const originalDimensions=capturedDimensionValues(source),sourceGlobals=solveEquations(source.equations.filter(row=>row.globalVariable),{}),nextGlobals=model.globalVariables,newDimensions=Object.fromEntries(model.dimensions.map(row=>[row.dimension,row.valueMm??row.value]));
  const guideDimensions=['D1@草图1','D1@草图2','D2@草图2'];
  function dimensionsValid(globals,dimensions){return Math.abs(dimensions[guideDimensions[0]]-globals['基架高度']/2)<1e-6&&Math.abs(dimensions[guideDimensions[1]]-globals['基架高度']/2)<1e-6&&Math.abs(dimensions[guideDimensions[2]]-globals['基架宽度']/2)<1e-6;}
  if(!dimensionsValid(sourceGlobals,originalDimensions)||!dimensionsValid(nextGlobals,newDimensions))throw new DefinitionError('常规基架原安装草图的宽高联动不一致');
  function coordinateOrigin(name,globals,dimensions){
    const length=globals['基架长度'],height=globals['基架高度'];
    if(['坐标系-进1','坐标系-出2'].includes(name))return [-length/2,height/2,0];
    if(['坐标系-出1','坐标系-进2'].includes(name))return [length/2,height/2,0];
    const support=/^绝缘子支架坐标系([1-3])$/.exec(name);if(support)return [dimensions['D'+support[1]+'@草图3']-length/2,41.5,0];
    const hanging=/^绝缘子吊装坐标系([1-3])$/.exec(name);if(hanging)return [dimensions['D'+hanging[1]+'@绝缘子吊装控制草图']-length/2,height-4,0];
    if(name==='吊杆配合坐标系')return [0,0,0];
    throw new DefinitionError('常规基架包含未知安装坐标系：'+name);
  }
  for(const coordinate of source.features.filter(feature=>feature.type==='CoordSys')){
    const properties=coordinate.definition?.properties,originalTransform=properties?.Transform?.transform;
    if(!originalTransform||properties.XAxisEntities||properties.YAxisEntities||properties.ZAxisEntities)throw new DefinitionError('常规基架坐标系旋转不再是原全局轴');
    const sourceOrigin=swMatrix(originalTransform).slice(12,15),expected=coordinateOrigin(coordinate.name,sourceGlobals,originalDimensions),next=coordinateOrigin(coordinate.name,nextGlobals,{...originalDimensions,...newDimensions});
    if(!next.every(Number.isFinite)||!near(sourceOrigin,expected))throw new DefinitionError('常规基架坐标系未复现原控制草图位置：'+coordinate.name);
    const guide=source.features.filter(feature=>feature.sketch&&coordinate.parents.includes(feature.name));
    if(coordinate.name!=='吊杆配合坐标系'){
      if(guide.length!==1)throw new DefinitionError('常规基架安装坐标没有唯一原控制草图');
      const origin=guide[0].sketch.points.filter(point=>point.id===properties.OriginEntity?.id);
      if(origin.length!==1||!near(transformPoint(origin[0].coordinates.map(value=>value*1000),inverseRigidMatrix(swMatrix(guide[0].sketch.modelToSketch))),sourceOrigin))throw new DefinitionError('常规基架坐标原点与控制草图身份不一致');
    }
    const target=updated.features.find(feature=>feature.id===coordinate.id),transform=[...originalTransform];for(let axis=0;axis<3;axis++)transform[9+axis]=next[axis]/1000;
    target.definition.properties.Transform.transform=transform;target.offlineCoordinateSolution={source:'original-024-control-sketch-equations',originMm:next,rotationPreserved:true,sourceGuide:guide[0]?.name??'原点'};
  }
  return true;
}
