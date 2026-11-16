import {DefinitionError} from './expressions.mjs';
import {planNativeEquationDrives} from './native-design-drives.mjs';

const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'缺失或不唯一');return rows[0];};

// Root equations own wall width/height. The bracket equation owns the channel
// length; leaf equations own slot counts and pitches. Keep their source roles.
export async function buildWallBracketDesign(scene,configuration,rootDimensions,read){
  const component=one(configuration.components.filter(component=>component.name==='5GX.000.002-1'),'穿墙支架原实例');
  if(!/^模板\/面对墙模板\(柜[前后]向墙\)\/支架\/5GX\.000\.002\.SLDASM$/.test(component.path))throw new DefinitionError('穿墙支架来源不符');
  const template=await read(component.path),source=one(template.configurations.filter(row=>row.name===component.configuration),'穿墙支架配置'),models=[];
  for(const stem of ['8GX.160.024','8GX.160.025']){
    const instances=source.components.filter(row=>row.path.endsWith('/'+stem+'.SLDPRT'));
    if(instances.length!==2||new Set(instances.map(row=>JSON.stringify([row.path,row.configuration]))).size!==1)throw new DefinitionError('穿墙支架缺少同源双边框');
    const part=await read(instances[0].path),partConfiguration=one(part.configurations.filter(row=>row.name===instances[0].configuration),'穿墙边框配置');
    const input=one(rootDimensions.filter(row=>row.dimension==='D2@基体-法兰1'&&row.instancePath[0]?.id===component.id&&row.instancePath.at(-1)?.path===part.source.path),'墙洞对边框的原驱动');
    if(!(input.valueMm>0))throw new DefinitionError('穿墙边框长度必须为正');
    const drives=await planNativeEquationDrives(part,partConfiguration,{},read,{dimensionInputs:{[input.dimension]:input.valueMm}});
    models.push({modelId:stem,templatePath:part.source.path,sourceSha256:part.source.sha256,configuration:partConfiguration.name,...drives,externalDimensions:[input]});
  }
  const horizontal=models.find(model=>model.modelId==='8GX.160.025'),channelDrives=await planNativeEquationDrives(template,source,{},read,{dimensionInputs:{'D2@基体-法兰1@8GX.160.025<2>.Part':horizontal.externalDimensions[0].valueMm}}),channel=one(source.components.filter(row=>row.path.endsWith('/8ZX.202.520.SLDPRT')),'穿墙槽钢实例'),channelTemplate=await read(channel.path);
  if(channelDrives.dimensions.length!==1||channelDrives.dimensions[0].templatePath!==channel.path||channelDrives.dimensions[0].dimension!=='D1@草图1')throw new DefinitionError('穿墙支架槽钢方程未对应原路径');
  models.push({modelId:'8ZX.202.520',templatePath:channel.path,sourceSha256:channelTemplate.source.sha256,configuration:channel.configuration,globalVariables:{},resolvedGlobalVariables:{},dimensions:channelDrives.dimensions,externalDimensions:[]});
  return {templatePath:template.source.path,sourceSha256:template.source.sha256,configuration:source.name,sourceScene:scene.source.path,rootInstance:component.name,models,placements:source.components.filter(row=>models.some(model=>model.templatePath===row.path&&model.configuration===row.configuration)).map(row=>({modelId:models.find(model=>model.templatePath===row.path).modelId,componentId:row.id,instance:row.name,installationMateNames:source.features.filter(feature=>!feature.suppressed&&feature.mateDefinition?.entities.some(entity=>entity.component?.split('/')[0]===row.name)).map(feature=>feature.name)}))};
}
