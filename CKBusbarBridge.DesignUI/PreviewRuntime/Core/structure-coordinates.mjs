import {DefinitionError,solveEquations} from './expressions.mjs';
import {capturedDimensionValues} from './equation-context.mjs';
import {swMatrix,inverseRigidMatrix,transformPoint} from './geometry.mjs';
import {baseTemplatePaths,frameTemplatePaths} from './structure-sources.mjs';
import {driveRegularFrameCoordinates} from './regular-frame-coordinates.mjs';
const near=(first,second)=>Math.hypot(...first.map((value,axis)=>value-second[axis]))<1e-5;
const kindByPath=new Map([...Object.entries(baseTemplatePaths),...Object.entries(frameTemplatePaths)].map(([kind,path])=>[path,kind]));

// Original guide dimensions and native model axes. These are schematic
// mounting datums: preserve the captured orientation and section offsets,
// then move the origin with its governing business dimensions. Every rule
// must first reproduce the stored source origin and native guide-point ID.
export function driveStructureCoordinates(source,updated,model){
  if(driveRegularFrameCoordinates(source,updated,model))return true;
  const kind=kindByPath.get(model.templatePath);if(!kind)return false;
  const sourceGlobals=solveEquations(source.equations.filter(row=>row.globalVariable),{}),oldDimensions=capturedDimensionValues(source),nextDimensions={...oldDimensions,...Object.fromEntries(model.dimensions.map(row=>[row.dimension,row.valueMm??row.value]))};
  const base=['Base','Base1','Base2'].includes(kind),elevated=kind==='ElevatedBase';
  function origin(name,globals,dimensions){
    const height=globals[base?'基座高':elevated?'高度':'基架高度'],width=globals[base?'基座宽':elevated?'宽度':'基架宽度'],depth=globals[base?'基座深':'深度'],length=globals['基架长度'];
    if(base){
      const z=kind==='Base'?0:-.75;
      if(name==='安装坐标系')return [-depth/2+21.5,-height/2,z];
      if(name==='基座安装坐标系')return [depth/2,height/2-dimensions['D1@基座安装坐标系草图'],z];
      if(kind==='Base'&&name==='绝缘子安装坐标系')return [-depth/2+41.5,dimensions['D1@绝缘子安装坐标系草图']-height/2,0];
      const side=/^安装坐标系\(([AB])\)$/.exec(name);
      if(kind==='Base2'&&side)return [.25,-height/2,z+(side[1]==='A'?1:-1)*(width/2-globals['挡板距边距离']-21.5)];
      const support=/^绝缘子安装坐标系([1-3])\(([AB])\)$/.exec(name);
      if(kind==='Base1'&&support)return [-2.5,dimensions['D'+support[1]+'@绝缘子安装坐标系草图'+support[2]]-height/2,z+(support[2]==='A'?1:-1)*(width/2-47.5)];
    }else if(elevated){
      if(name==='下'||name==='上')return [(name==='下'?1:-1)*height/2,-depth/2+21.5,0];
      if(name==='绝缘子安装坐标系')return [height/2-dimensions['D1@草图2'],-depth/2+41.5,0];
    }else{
      const inlet=['坐标系-进1','坐标系-出2'].includes(name),outlet=['坐标系-出1','坐标系-进2'].includes(name);
      if(inlet||outlet){
        if(kind==='FaceToFaceLargeOffsetCorner028')return inlet?[length/2,0,0]:[0,0,-width/2];
        let y=height/2,z=0,x=(outlet?1:-1)*length/2;
        if(kind==='SideToSidePhaseChange029')y=width/2;
        if(kind==='FaceToFaceSmallOffset032')z=(globals[inlet?'进孔偏移方向':'出孔偏移方向']===1?1:-1)*globals[inlet?'进孔偏移尺寸':'出孔偏移尺寸'];
        if(kind==='FaceToWallLarge1'){x+=1;if(inlet)z=(globals['上侧封板宽度']-globals['下侧封板宽度'])/2;}
        if(kind==='FaceToWallLarge2'){x+=1;if(inlet)y=(height-globals['侧盖板宽度'])/2;}
        return [x,y,z];
      }
      const support=/^绝缘子支架坐标系([1-3])$/.exec(name);
      if(support){const station=dimensions['D'+support[1]+'@草图3'];return kind==='FaceToFaceLargeOffsetCorner028'?[length/2-station,-height/2+47.5,0]:[station-length/2+(['FaceToWallLarge1','FaceToWallLarge2'].includes(kind)?1:0),41.5,0];}
      if(name==='吊杆配合坐标系')return kind==='SideToSidePhaseChange029'?[0,width/2,height/2]:[0,0,0];
    }
    throw new DefinitionError('结构示意安装坐标没有对应原控制规则：'+kind+'/'+name);
  }
  for(const coordinate of source.features.filter(feature=>feature.type==='CoordSys')){
    const properties=coordinate.definition?.properties,originalTransform=properties?.Transform?.transform;
    if(!originalTransform)throw new DefinitionError('结构安装坐标缺少原旋转矩阵：'+coordinate.name);
    const oldOrigin=swMatrix(originalTransform).slice(12,15),expected=origin(coordinate.name,sourceGlobals,oldDimensions),next=origin(coordinate.name,model.globalVariables,nextDimensions);
    if(!near(oldOrigin,expected)||!next.every(Number.isFinite))throw new DefinitionError('结构安装控制规则没有复现原坐标：'+kind+'/'+coordinate.name);
    const guides=source.features.filter(feature=>feature.sketch&&coordinate.parents.includes(feature.name));
    if(guides.length){
      const matches=guides.flatMap(guide=>guide.sketch.points.filter(point=>point.id===properties.OriginEntity?.id).map(point=>({guide,point})));
      if(matches.length!==1||!near(transformPoint(matches[0].point.coordinates.map(value=>value*1000),inverseRigidMatrix(swMatrix(matches[0].guide.sketch.modelToSketch))),oldOrigin))throw new DefinitionError('结构坐标原点与原控制草图身份不一致：'+kind+'/'+coordinate.name);
    }
    const target=updated.features.find(feature=>feature.id===coordinate.id),transform=[...originalTransform];for(let axis=0;axis<3;axis++)transform[9+axis]=next[axis]/1000;
    target.definition.properties.Transform.transform=transform;target.offlineCoordinateSolution={source:'original-structure-guide-schematic-datum',originMm:next,rotationPreserved:true,sourceGuide:guides[0]?.name??'原结构端部基准',schematic:true};
  }
  return true;
}
