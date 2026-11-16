import {DefinitionError} from './expressions.mjs';
import {solveStructureEquations} from './structure-equations.mjs';
import {driveStructureCoordinates} from './structure-coordinates.mjs';
import {baseTemplatePaths,frameTemplatePaths} from './structure-sources.mjs';
import {inverseRigidMatrix,swMatrix,transformPoint} from './geometry.mjs';
import {baseTemplate,frameTemplate} from './fixed-structure-templates.mjs';

const kinds=new Map([...Object.entries(baseTemplatePaths),...Object.entries(frameTemplatePaths)].map(([kind,path])=>[path,kind]));
export const hasFixedStructureDatums=path=>kinds.has(path);

export async function deriveFixedStructureDatums(source,name,input,read){
  const graph=await solveStructureEquations(source,name,input.globalVariables,read,
    {rootDimensions:input.rootDimensions,businessDrive:input.businessDrive});
  const original=source.configurations.find(row=>row.name===name),configuration=structuredClone(original),
    model=graph.models.find(row=>row.templatePath===source.source.path&&row.configuration===name);
  if(!model||!driveStructureCoordinates(original,configuration,model))
    throw new DefinitionError('固定模板没有可用的安装基准：'+source.source.path);
  const datum=name=>configuration.features.find(row=>row.type==='CoordSys'&&row.name===name&&!row.suppressed);
  const coordinate=name=>{
    const feature=datum(name);
    if(!feature)throw new DefinitionError('固定结构缺少安装坐标：'+name);
    return swMatrix(feature.definition.properties.Transform.transform).slice(12,15);
  };
  const globals=model.globalVariables,kind=kinds.get(source.source.path),isBase=Object.hasOwn(baseTemplatePaths,kind);
  if(!kind)throw new DefinitionError('固定外壳没有已核验的模板方向：'+source.source.path);
  const parameters=Object.entries(globals).map(([name,value])=>({name,value,editable:false}));
  let box,port,openingHeight,mounts,shapeToNative=null,openings=[];
  if(kind==='ElevatedBase'){
    const [depth,height,width]=['深度','高度','宽度'].map(name=>globals[name]);
    // The stacked riser is X-down in the native template and open vertically.
    shapeToNative=[0,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1];
    box={min:[-depth/2,-height/2,-width/2],max:[depth/2,height/2,width/2]};
  }else if(isBase){
    const [depth,height,width]=['基座深','基座高','基座宽'].map(name=>globals[name]);
    port=coordinate('基座安装坐标系');openingHeight=globals['基座开孔高度'];
    const centerZ=port[2];
    // The installed bridge begins at the port plane. A flange cannot extend past
    // that plane into the first frame, or below the cabinet's mating roof.
    const [low,high]=port[0]>0?[port[0]-depth,port[0]]:[port[0],port[0]+depth];
    box={min:[low,-height/2,centerZ-width/2],max:[high,height/2,centerZ+width/2]};
  }else{
    const [length,height,width]=['基架长度','基架高度','基架宽度'].map(name=>globals[name]);
    const sideMounted=kind==='SideToSidePhaseChange029',corner=kind==='FaceToFaceLargeOffsetCorner028';
    if(sideMounted)shapeToNative=[1,0,0,0,0,0,-1,0,0,1,0,0,0,width/2,0,1];
    const fromNative=shapeToNative?inverseRigidMatrix(shapeToNative):null;
    const canonical=name=>fromNative?transformPoint(coordinate(name),fromNative):coordinate(name);
    const inlet=canonical('坐标系-进1'),outlet=canonical('坐标系-出1');
    const x=corner?-length/2:Math.min(inlet[0],outlet[0]),X=corner?length/2:Math.max(inlet[0],outlet[0]);
    const y=corner||sideMounted?-height/2:0,Y=y+height;
    box={min:[x,y,-width/2],max:[X,Y,width/2]};
    if(kind==='FaceToFaceSmallOffset032')openings=[inlet,outlet].map(point=>({
      station:point[0],low:point[2]-(width/2-Math.abs(point[2])),high:point[2]+(width/2-Math.abs(point[2])),top:Y
    }));
    if(kind==='FaceToWallLarge1')openings=[{station:inlet[0],
      low:-width/2+globals['上侧封板宽度'],high:width/2-globals['下侧封板宽度'],top:Y}];
    if(kind==='FaceToWallLarge2')openings=[{station:inlet[0],low:-width/2,high:width/2,top:Y-globals['侧盖板宽度']}];
    mounts=[];
    for(let index=1;index<=3;index++)if(Object.hasOwn(input.rootDimensions,'D'+index+'@草图3')){
      const suspended=Object.hasOwn(input.rootDimensions,'D'+index+'@绝缘子吊装控制草图');
      mounts.push({point:canonical('绝缘子'+(suspended?'吊装':'支架')+'坐标系'+index),suspended});
    }
  }
  // The outward assembly contains its compatibility datums, never its old
  // welded/sheet-metal children. Remaining graph inputs are equations only.
  configuration.components=[];configuration.features=configuration.features.filter(row=>row.type==='CoordSys');
  const template={...source,configurations:[configuration]};
  return {template,parameters,kind,isBase,box,port,openingHeight,mounts,shapeToNative,openings,graph};
}

export function generateFromFixedDatums(kernel,derived,casing){
  const result=derived.isBase?baseTemplate(kernel,derived.box,derived.openingHeight,derived.port,{openTop:derived.kind==='ElevatedBase'}):
    frameTemplate(kernel,derived.box,derived.mounts,casing,{corner:derived.kind==='FaceToFaceLargeOffsetCorner028',
      sideMounted:derived.kind==='SideToSidePhaseChange029',openings:derived.openings});
  if(derived.shapeToNative)try{
    for(const [index,solid] of result.solids.entries()){
      result.solids[index]=solid.transform(derived.shapeToNative);solid.delete();
    }
  }catch(error){result.solids.forEach(solid=>solid.delete());throw error;}
  return result;
}
