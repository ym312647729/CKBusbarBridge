import {DefinitionError} from './expressions.mjs';
import {solveStructureEquations} from './structure-equations.mjs';
import {driveStructureCoordinates} from './structure-coordinates.mjs';
import {baseTemplatePaths,frameTemplatePaths} from './structure-sources.mjs';
import {swMatrix} from './geometry.mjs';
import {baseTemplate,frameTemplate} from './fixed-structure-templates.mjs';

export const hasFixedStructureDatums=path=>path===baseTemplatePaths.Base||path===frameTemplatePaths.Regular024;

export async function deriveFixedStructureDatums(source,name,input,read){
  const graph=await solveStructureEquations(source,name,input.globalVariables,read,
    {rootDimensions:input.rootDimensions,businessDrive:input.businessDrive});
  const original=source.configurations.find(row=>row.name===name),configuration=structuredClone(original),
    model=graph.models.find(row=>row.templatePath===source.source.path&&row.configuration===name);
  if(!model||!driveStructureCoordinates(original,configuration,model))
    throw new DefinitionError('固定模板没有可用的安装基准：'+source.source.path);
  const datum=name=>configuration.features.find(row=>row.type==='CoordSys'&&row.name===name&&!row.suppressed);
  const coordinate=name=>swMatrix(datum(name).definition.properties.Transform.transform).slice(12,15);
  const globals=model.globalVariables;
  const parameters=Object.entries(globals).map(([name,value])=>({name,value,editable:false}));
  let box,port,openingHeight,mounts;
  if(source.source.path===baseTemplatePaths.Base){
    const [depth,height,width]=['基座深','基座高','基座宽'].map(name=>globals[name]);
    const centerZ=coordinate('安装坐标系')[2];
    port=coordinate('基座安装坐标系');openingHeight=globals['基座开孔高度'];
    // The installed bridge begins at the port plane. A flange cannot extend past
    // that plane into the first frame, or below the cabinet's mating roof.
    const [low,high]=port[0]>0?[port[0]-depth,port[0]]:[port[0],port[0]+depth];
    box={min:[low,-height/2,centerZ-width/2],max:[high,height/2,centerZ+width/2]};
  }else{
    const [length,height,width]=['基架长度','基架高度','基架宽度'].map(name=>globals[name]);
    box={min:[-length/2,0,-width/2],max:[length/2,height+12,width/2]};
    mounts=[];
    for(let index=1;index<=3;index++)if(Object.hasOwn(input.rootDimensions,'D'+index+'@草图3')){
      const suspended=Object.hasOwn(input.rootDimensions,'D'+index+'@绝缘子吊装控制草图');
      mounts.push({point:coordinate('绝缘子'+(suspended?'吊装':'支架')+'坐标系'+index),suspended});
    }
  }
  // The outward assembly contains its compatibility datums, never its old
  // welded/sheet-metal children. Remaining graph inputs are equations only.
  configuration.components=[];configuration.features=configuration.features.filter(row=>row.type==='CoordSys');
  const template={...source,configurations:[configuration]};
  return {template,parameters,box,port,openingHeight,mounts,graph};
}

export function generateFromFixedDatums(kernel,derived){
  return derived.port?baseTemplate(kernel,derived.box,derived.openingHeight,derived.port):
    frameTemplate(kernel,derived.box,derived.mounts);
}
