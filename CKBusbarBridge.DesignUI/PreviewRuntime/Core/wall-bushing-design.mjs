import {DefinitionError} from './expressions.mjs';
import {planNativeEquationDrives} from './native-design-drives.mjs';

export async function buildWallBushingDesign(scene,configuration,record,read){
  const globals=Object.fromEntries([['φ','Diameter'],...Array.from({length:10},(_,index)=>['C'+(index+1),'C'+(index+1)])].map(([name,field])=>[name,record.values['General.WallBushingTerminal.'+field]]));
  if(Object.values(globals).some(value=>!Number.isFinite(value)))throw new DefinitionError('穿墙套管的原业务参数不完整');
  const components=configuration.components.filter(component=>/^穿墙套管-\d+$/.test(component.name));
  if(components.length!==3||new Set(components.map(component=>JSON.stringify([component.path,component.configuration]))).size!==1)throw new DefinitionError('原场景缺少共用定义的三相穿墙套管');
  const component=components[0],template=await read(component.path),source=template.configurations.find(source=>source.name===component.configuration);
  if(!source||template.source.path!==component.path||!/^模板\/面对墙模板\(柜[前后]向墙\)\/柜体场景示意\/穿墙套管\.SLDPRT$/.test(component.path))throw new DefinitionError('穿墙套管不是当前原场景的源定义');
  const drives=await planNativeEquationDrives(template,source,globals,read),model={modelId:'wall-bushing',templatePath:component.path,sourceSha256:template.source.sha256,configuration:source.name,...drives};
  return {group:'General.WallBushingTerminal',globalVariables:globals,models:[model],placements:components.map(component=>({modelId:model.modelId,componentId:component.id,instance:component.name,installationMateNames:configuration.features.filter(feature=>!feature.suppressed&&feature.mateDefinition?.entities.some(entity=>entity.componentId===component.id||entity.component?.split('/')[0]===component.name)).map(feature=>feature.name)})),sourceScene:scene.source.path};
}

// The root equation H + J - C1 reads the child terminal length. Substituting
// the captured C1 would silently break this coupling after a business edit.
export function wallBushingEquationInitialValues(context,plan){
  const values={...context.initialValues};
  for(const reference of context.referenceEvidence){
    const leaf=reference.instancePath.at(-1),model=plan.models.find(model=>model.templatePath===leaf?.path&&model.configuration===leaf.configuration);
    if(!model)continue;const dimension=model.dimensions.find(dimension=>dimension.dimension===reference.dimension);if(dimension)values[reference.reference]=dimension.valueMm;
  }
  return values;
}

export function bindWallBushingExternalDimensions(plan,rootDimensions){
  for(const model of plan.models)model.externalDimensions=rootDimensions.filter(dimension=>{const leaf=dimension.instancePath.at(-1);return leaf?.path===model.templatePath&&leaf.configuration===model.configuration;});
  return plan;
}
