import {DefinitionError} from './expressions.mjs';

export function capturedDimensionValues(configuration){
  const capturedValues={};
  for(const feature of configuration.features)for(const dimension of feature.dimensions){
    const localName=dimension.name.split('@').slice(0,-1).join('@');
    const value=dimension.dimensionType===2?dimension.value:dimension.value*1000;
    if(Object.hasOwn(capturedValues,localName)&&Math.abs(capturedValues[localName]-value)>1e-7)throw new DefinitionError('同名源尺寸存在不同数值：'+localName);
    capturedValues[localName]=value;
  }
  return capturedValues;
}

function componentReference(token){
  const numbered=/^(.+)<(\d+)>\.(Part|Assembly)$/i.exec(token);
  if(numbered)return {name:numbered[1]+'-'+numbered[2],kind:numbered[3].toLowerCase()};
  const nativeName=/^(.+-\d+)\.(Part|Assembly)$/i.exec(token);
  if(nativeName)return {name:nativeName[1],kind:nativeName[2].toLowerCase()};
  throw new DefinitionError('装配方程的组件限定名尚未支持：'+token);
}

export async function resolveDimensionReference(template,configuration,reference,loadTemplate){
  const terms=reference.split('@');
  if(terms.length<2)throw new DefinitionError('尺寸限定名缺少原始草图或特征：'+reference);
  const dimensionName=terms.slice(0,2).join('@'),instances=terms.slice(2).reverse().map(componentReference),instancePath=[];
  let selectedConfiguration=configuration,selectedTemplate=template;
  for(const instance of instances){
    const candidates=selectedConfiguration.components.filter(component=>component.name.split('/').at(-1)===instance.name);
    if(candidates.length!==1)throw new DefinitionError('方程组件路径无法唯一对应：'+reference,{path:template.source.path,instance:instance.name});
    const component=candidates[0];
    if(component.isVirtual){
      if(!component.embeddedDefinition?.captured)throw new DefinitionError('方程引用的内嵌组件缺少源定义：'+reference);
      selectedTemplate={...component.embeddedDefinition,source:{...component.embeddedDefinition.source,path:selectedTemplate.source.path+'#embedded/'+component.id}};
    }else{
      if(typeof loadTemplate!=='function')throw new DefinitionError('装配方程缺少源模板读取入口：'+reference);
      selectedTemplate=await loadTemplate(component.path);
    }
    selectedConfiguration=selectedTemplate.configurations.find(item=>item.name===component.configuration);
    if(!selectedConfiguration)throw new DefinitionError('方程组件配置不存在：'+reference);
    if(!component.isVirtual&&!component.path.toLowerCase().endsWith(instance.kind==='assembly'?'.sldasm':'.sldprt'))throw new DefinitionError('方程组件文档类型与源配置不符：'+reference);
    instancePath.push({name:component.name,id:component.id,path:selectedTemplate.source.path,configuration:component.configuration});
  }
  const dimensions=selectedConfiguration.features.flatMap(feature=>feature.dimensions.filter(dimension=>dimension.name.split('@').slice(0,-1).join('@')===dimensionName).map(dimension=>({featureId:feature.id,featureName:feature.name,...dimension})));
  if(!dimensions.length)throw new DefinitionError('方程引用的组件源尺寸不存在：'+reference);
  const values=capturedDimensionValues(selectedConfiguration);
  return {reference,dimension:dimensionName,instancePath,value:values[dimensionName],template:selectedTemplate,configuration:selectedConfiguration,dimensions};
}

// Resolve only explicit native instance paths. No nearest name, sibling or numeric fallback.
export async function equationInitialValues(template,configuration,loadTemplate){
  const initialValues=capturedDimensionValues(configuration),templateCache=new Map();
  const requestedReferences=new Set();
  for(const equation of configuration.equations){
    if(equation.disabled||equation.userDisposition?.mode==='void-by-user')continue;
    const text=typeof equation==='string'?equation:equation.expression;
    const assignment=/^\s*"[^"]+"\s*=\s*(.*)$/.exec(text);
    if(!assignment)continue; // The equation parser reports malformed assignments.
    for(const match of assignment[1].matchAll(/"([^"]+)"/g)){
      const reference=match[1];
      if(reference.split('@').length>2&&/\.(Part|Assembly)$/i.test(reference)&&!Object.hasOwn(initialValues,reference))requestedReferences.add(reference);
    }
  }
  const referenceEvidence=[];
  for(const reference of requestedReferences){
    const cachedLoad=path=>{if(!templateCache.has(path))templateCache.set(path,loadTemplate(path));return templateCache.get(path);};
    const resolved=await resolveDimensionReference(template,configuration,reference,typeof loadTemplate==='function'?cachedLoad:undefined);
    initialValues[reference]=resolved.value;
    referenceEvidence.push({reference,dimension:resolved.dimension,instancePath:resolved.instancePath,value:resolved.value,source:'captured-native-dimension-at-source-configuration'});
  }
  return {initialValues,referenceEvidence};
}
