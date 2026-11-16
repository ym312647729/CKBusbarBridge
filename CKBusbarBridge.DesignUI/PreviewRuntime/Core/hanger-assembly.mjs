import {DefinitionError} from './expressions.mjs';
import {planAssemblyDimensionDrives} from './equation-targets.mjs';
import {driveStraightWeldDefinition,weldReferenceDisplacement} from './driven-weld.mjs';
import {solveAssemblyTranslations} from './assembly-translations.mjs';
import {swMatrix,inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';
import {transformDirection} from './template-planes.mjs';
const identity=()=>[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];

export async function deriveHangerAssembly(sourceTemplate,configurationName,globals,readSource){
  if(sourceTemplate.source.path!=='模板/吊杆模板/5GX.000.001.SLDASM')throw new DefinitionError('吊杆驱动入口未对应原始业务模板');
  if(Object.keys(globals).some(name=>!['宽度','角钢高度'].includes(name)))throw new DefinitionError('吊杆驱动变量未对应原始业务规则');
  const sourceCache=new Map([[sourceTemplate.source.path,sourceTemplate]]),read=async path=>{if(!sourceCache.has(path))sourceCache.set(path,await readSource(path));return sourceCache.get(path);},specification=await planAssemblyDimensionDrives(sourceTemplate,configurationName,globals,read),infos=new Map(),definitions=new Map(),evidence=[];
  const rootConfiguration=sourceTemplate.configurations.find(row=>row.name===configurationName),coordinates=rootConfiguration.features.filter(row=>row.type==='CoordSys'&&row.name==='吊杆配合坐标系'&&!row.suppressed);
  if(coordinates.length!==1)throw new DefinitionError('受驱吊杆没有唯一根安装坐标系');
  const coordinate=coordinates[0],coordinateComponents=rootConfiguration.components.filter(row=>coordinate.parents.includes(row.name)),guides=rootConfiguration.features.filter(row=>row.sketch&&coordinate.parents.includes(row.name)),originId=coordinate.definition?.properties.OriginEntity?.id;
  if(coordinateComponents.length!==1||!coordinateComponents[0].fixedPosition||coordinateComponents[0].path!=='模板/吊杆模板/8ZX.202.004.SLDPRT'||guides.length!==1)throw new DefinitionError('吊杆安装坐标系未唯一依赖原始固定横梁');
  const guide=guides[0].sketch;
  if(guide.segments.length!==1||!guide.segments[0].construction||guide.segments[0].type!==0||!guide.points.some(row=>row.id===originId)||guide.relations.some(row=>row.suppressed||row.dimension||![5,9,12].includes(row.type))||guide.relations.length!==4||guide.relations.filter(row=>row.type===12).length!==2||!guide.relations.some(row=>row.type===12&&row.entities.some(entity=>entity.id===originId)&&row.entities.some(entity=>entity.id===guide.segments[0].id))||!guide.relations.some(row=>row.type===12&&row.entities.some(entity=>entity.lineParameters&&entity.start&&entity.end))||!guide.relations.some(row=>row.type===9&&row.entities.some(entity=>entity.point?.every(value=>Math.abs(value)<1e-12))))throw new DefinitionError('吊杆根安装坐标系的固定原点及中点依赖尚未核验');
  evidence.push({path:sourceTemplate.source.path,feature:coordinate.name,source:'fixed-beam-origin-and-midpoint-coordinate-invariant',rotationChanged:false});
  const key=(path,name)=>JSON.stringify([path,name]);
  async function childTemplate(owner,component){return component.isVirtual?{...component.embeddedDefinition,source:{...component.embeddedDefinition?.source,path:owner.source.path+'#embedded/'+component.id}}:read(component.path);}
  async function visit(template,name,ancestors=[]){
    const identityKey=key(template.source.path,name);if(ancestors.includes(identityKey))throw new DefinitionError('受驱吊杆组件引用形成循环');if(infos.has(identityKey))return infos.get(identityKey);
    const configuration=template.configurations.find(row=>row.name===name);if(!configuration)throw new DefinitionError('受驱吊杆组件源配置不存在');
    const drives=specification.documentDrives.filter(row=>row.templatePath===template.source.path&&row.configuration===name&&row.changed);
    let derived=template,motion=null,changed=false;
    if(configuration.components.length){
      const children=new Map();for(const component of configuration.components.filter(row=>row.suppression!==0))children.set(component.id,await visit(await childTemplate(template,component),component.configuration,[...ancestors,identityKey]));
      changed=[...children.values()].some(info=>info.changed);
      if(changed){
        async function displacement(entity){
          const names=entity.component.split('/');let originalTemplate=template,originalConfiguration=configuration,updatedConfiguration=configuration,originalPlacement=identity(),updatedPlacement=identity(),selectedInfo;
          for(const [index,nativeName]of names.entries()){
            const matches=originalConfiguration.components.filter(row=>row.suppression!==0&&row.name.split('/').at(-1)===nativeName);
            if(matches.length!==1)throw new DefinitionError('受驱吊杆配合的嵌套实例无法唯一对应：'+entity.component);
            const component=matches[0],updatedComponent=updatedConfiguration.components.find(row=>row.id===component.id);if(!updatedComponent)throw new DefinitionError('受驱吊杆组件身份发生变化');
            originalPlacement=multiplyMatrix(originalPlacement,swMatrix(component.transform));updatedPlacement=multiplyMatrix(updatedPlacement,swMatrix(index===0?component.transform:updatedComponent.transform));
            originalTemplate=await childTemplate(originalTemplate,component);selectedInfo=infos.get(key(originalTemplate.source.path,component.configuration));if(!selectedInfo)throw new DefinitionError('受驱吊杆配合组件尚未求解');
            originalConfiguration=originalTemplate.configurations.find(row=>row.name===component.configuration);updatedConfiguration=selectedInfo.template.configurations.find(row=>row.name===component.configuration);
            if(index===names.length-1&&component.id!==entity.componentId)throw new DefinitionError('受驱吊杆配合叶组件ID不符');
          }
          const originalPoint=entity.parameters.slice(0,3).map(value=>value*1000),inverse=inverseRigidMatrix(originalPlacement),localPoint=transformPoint(originalPoint,inverse),localDirection=transformDirection(entity.parameters.slice(3,6),inverse),referenceType=entity.referenceType===2&&Array.isArray(entity.reference?.circleParameters)?4:entity.referenceType,shapeDelta=selectedInfo.motion?weldReferenceDisplacement(selectedInfo.motion,localPoint,localDirection,referenceType,entity.parameters[6]*1000):[0,0,0],newPoint=transformPoint(localPoint.map((value,index)=>value+shapeDelta[index]),updatedPlacement);
          return newPoint.map((value,index)=>value-originalPoint[index]);
        }
        const solution=await solveAssemblyTranslations(configuration,displacement);derived=structuredClone(template);const updated=derived.configurations.find(row=>row.name===name);
        for(const component of updated.components){const translation=solution.translations.find(row=>row.componentId===component.id);if(translation)for(let axis=0;axis<3;axis++)component.transform[9+axis]+=translation.delta[axis]/1000;if(component.isVirtual&&children.has(component.id))component.embeddedDefinition=children.get(component.id).template;}
        updated.offlineAssemblySolution=solution;evidence.push({path:template.source.path,configuration:name,...solution});
      }
    }else if(drives.length){
      if(drives.length!==1)throw new DefinitionError('同一吊杆零件的多尺寸联合驱动尚未支持');
      const result=driveStraightWeldDefinition(template,name,drives[0]);derived=result.template;motion=result.motion;changed=true;evidence.push({path:template.source.path,configuration:name,...result.evidence});
    }
    const info={template:derived,motion,changed};infos.set(identityKey,info);
    if(changed||!definitions.has(template.source.path)){
      const previous=definitions.get(template.source.path);if(previous&&changed){const combined=structuredClone(previous);combined.configurations[combined.configurations.findIndex(row=>row.name===name)]=derived.configurations.find(row=>row.name===name);definitions.set(template.source.path,combined);info.template=combined;}else definitions.set(template.source.path,derived);
    }
    return info;
  }
  const root=await visit(sourceTemplate,configurationName),template=structuredClone(root.template),configuration=template.configurations.find(row=>row.name===configurationName);
  for(const equation of configuration.equations)if(equation.globalVariable){const name=/^\s*"([^"]+)"/.exec(equation.expression)?.[1];if(Object.hasOwn(specification.globals,name))equation.expression='"'+name+'" = '+specification.globals[name];}
  definitions.set(template.source.path,template);
  for(const drive of specification.documentDrives.filter(row=>row.changed))if(!infos.get(key(drive.templatePath,drive.configuration))?.changed)throw new DefinitionError('吊杆方程目标未进入实际几何求解');
  return {template,loadTemplate:async path=>definitions.get(path)??read(path),specification,evidence,source:'captured-hanger-feature-and-mate-system',nativeGeometryEquivalenceVerified:false};
}
