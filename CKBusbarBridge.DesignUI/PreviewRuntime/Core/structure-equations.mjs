import {DefinitionError,parseExpression,evaluateExpression} from './expressions.mjs';
import {capturedDimensionValues,resolveDimensionReference} from './equation-context.mjs';
import {baseTemplatePaths,frameTemplatePaths} from './structure-sources.mjs';
import {prepareStructureBusinessDrives} from './structure-business-drives.mjs';
export {baseTemplatePaths} from './structure-sources.mjs';
const supported=new Set([...Object.values(baseTemplatePaths),...Object.values(frameTemplatePaths)]);
const key=(path,configuration,name)=>JSON.stringify([path,configuration,...(name===undefined?[]:[name])]);
const references=node=>node.type==='reference'?[node.name]:node.type==='binary'?[...references(node.left),...references(node.right)]:node.type==='unary'?references(node.operand):node.type==='call'?node.args.flatMap(references):[];
const same=(first,second)=>first===second||typeof first==='number'&&typeof second==='number'&&Math.abs(first-second)<1e-7;

// One graph for the native root, child assemblies and parts. A root reference
// to a child equation reads the child's new value, never its captured value.
export async function solveStructureEquations(root,configurationName,globalInputs,readSource,{rootDimensions={},businessDrive=null}={}){
  if(!supported.has(root.source.path))throw new DefinitionError('结构驱动不属于本项目基座或基架');
  const business=await prepareStructureBusinessDrives(root,configurationName,globalInputs,readSource,businessDrive);
  root=business.template;readSource=business.readTemplate;
  const documents=new Map(),assignments=new Map(),captured=new Map(),pending=new Set(),resolved=new Map(),skipped=[];
  async function visit(template,name,ancestors=[]){
    const identity=key(template.source.path,name);if(ancestors.includes(identity))throw new DefinitionError('结构定义组件循环');if(documents.has(identity))return documents.get(identity);
    const configuration=template.configurations.find(row=>row.name===name);if(!configuration)throw new DefinitionError('结构组件配置缺失');
    const document={template,configuration,identity,bindings:new Map(),equations:[]};documents.set(identity,document);
    for(const [dimension,value]of Object.entries(capturedDimensionValues(configuration)))captured.set(key(template.source.path,name,dimension),value);
    for(const component of configuration.components){
      if(component.suppression===0){skipped.push({templatePath:component.path,component:component.name,reason:'native-component-suppressed'});continue;}
      const child=component.isVirtual?{...component.embeddedDefinition,source:{...component.embeddedDefinition?.source,path:template.source.path+'#embedded/'+component.id}}:await readSource(component.path);
      if(child.representation?.mode==='deferred'){skipped.push({templatePath:child.source.path,reason:child.representation.reason??'user-deferred-native-template',policyId:child.representation.policyId});continue;}
      await visit(child,component.configuration,[...ancestors,identity]);
    }
    return document;
  }
  const rootDocument=await visit(root,configurationName);
  async function bind(document,name){
    if(document.bindings.has(name))return document.bindings.get(name);
    let identity;
    if(name.includes('@')){const target=await resolveDimensionReference(document.template,document.configuration,name,readSource);await visit(target.template,target.configuration.name);identity=key(target.template.source.path,target.configuration.name,target.dimension);}
    else identity=key(document.template.source.path,document.configuration.name,name);
    document.bindings.set(name,identity);return identity;
  }
  // Binding may discover explicitly referenced suppressed components. Iterate
  // the map so those definitions participate in the same dependency graph.
  for(const document of documents.values())for(const equation of document.configuration.equations.filter(row=>!row.disabled&&row.userDisposition?.mode!=='void-by-user')){
    const match=/^\s*"([^"]+)"\s*=\s*(.*)$/.exec(equation.expression);if(!match)throw new DefinitionError('结构原方程格式无效');
    const target=await bind(document,match[1]),expression=parseExpression(match[2]);for(const name of new Set(references(expression)))await bind(document,name);
    const row={target,expression,document,sourceEquation:equation.expression,equationIndex:equation.index,reference:match[1],globalVariable:equation.globalVariable};
    if(!assignments.has(target))assignments.set(target,[]);assignments.get(target).push(row);document.equations.push(row);
  }
  const allowed=new Set(rootDocument.equations.filter(row=>row.globalVariable).map(row=>row.reference));
  if(Object.entries(globalInputs).some(([name,value])=>!allowed.has(name)||!Number.isFinite(value)))throw new DefinitionError('结构输入未对应原始全局变量');
  for(const [name,value]of Object.entries(globalInputs))resolved.set(key(root.source.path,configurationName,name),value);
  for(const [dimension,value]of Object.entries(rootDimensions)){
    const identity=key(root.source.path,configurationName,dimension),native=rootDocument.configuration.features.flatMap(feature=>feature.dimensions).filter(row=>row.name.split('@').slice(0,-1).join('@')===dimension);
    if(!native.length||native.some(row=>row.dimensionType!==0)||!Number.isFinite(value)||value<0)throw new DefinitionError('结构业务定位尺寸未对应原始长度：'+dimension);
    resolved.set(identity,value);
    if(!assignments.has(identity))assignments.set(identity,[{document:rootDocument,sourceEquation:null,equationIndex:null,reference:dimension,globalVariable:false,source:'original-business-installation-position'}]);
  }
  function resolve(identity){
    if(resolved.has(identity))return resolved.get(identity);
    if(pending.has(identity))throw new DefinitionError('结构跨层方程循环：'+JSON.parse(identity).at(-1));
    const rows=assignments.get(identity);if(!rows){if(captured.has(identity))return captured.get(identity);throw new DefinitionError('结构跨层方程缺少引用：'+JSON.parse(identity).at(-1));}
    pending.add(identity);const values=rows.map(row=>evaluateExpression(row.expression,name=>resolve(row.document.bindings.get(name))));pending.delete(identity);
    if(values.some(value=>!Number.isFinite(value)&&!['suppressed','unsuppressed'].includes(value)))throw new DefinitionError('结构方程结果无效');
    if(values.some(value=>!same(value,values[0])))throw new DefinitionError('同一结构文档尺寸被多个原方程驱动为不同值：'+JSON.parse(identity).at(-1));resolved.set(identity,values[0]);return values[0];
  }
  for(const identity of assignments.keys())resolve(identity);
  const models=[];
  for(const document of documents.values()){
    const dimensions=[],featureStates=[],globalVariables={};
    for(const [identity,rows]of assignments){const [path,name,symbol]=JSON.parse(identity);if(path!==document.template.source.path||name!==document.configuration.name)continue;const value=resolve(identity),dimensionRows=document.configuration.features.flatMap(feature=>feature.dimensions.filter(dimension=>dimension.name.split('@').slice(0,-1).join('@')===symbol));
      const evidence=rows.map(row=>({ownerTemplatePath:row.document.template.source.path,ownerConfiguration:row.document.configuration.name,reference:row.reference,sourceEquation:row.sourceEquation,equationIndex:row.equationIndex,...row.source?{source:row.source}:{}}));
      if(dimensionRows.length){const types=new Set(dimensionRows.map(row=>row.dimensionType));if(types.size!==1||![0,2].includes([...types][0])||!Number.isFinite(value))throw new DefinitionError('结构驱动尺寸单位或结果不支持：'+symbol);const count=types.has(2);if(count&&(!Number.isInteger(value)||value<1))throw new DefinitionError('结构阵列数量必须是正整数');dimensions.push({dimension:symbol,...count?{unit:'count',value,sourceValue:captured.get(identity)}:{valueMm:value,sourceValueMm:captured.get(identity)},equations:evidence});}
      else if(['suppressed','unsuppressed'].includes(value)){if(document.configuration.features.filter(feature=>feature.name===symbol).length!==1)throw new DefinitionError('结构压缩方程未对应唯一原特征');featureStates.push({feature:symbol,suppressed:value==='suppressed',equations:evidence});}
      else if(rows.every(row=>row.globalVariable))globalVariables[symbol]=value;
      else throw new DefinitionError('结构方程目标未对应原尺寸或特征：'+symbol);
    }
    models.push({templatePath:document.template.source.path,sourceSha256:document.template.source.sha256,configuration:document.configuration.name,isAssembly:document.configuration.components.length>0,globalVariables,dimensions,featureStates});
  }
  return {version:'original-structure-equations/1',templatePath:root.source.path,configuration:configurationName,globalInputs:{...globalInputs},models,skipped,businessDrives:business.evidence,nativeModelValidationRequired:false,sourceTemplatesRechecked:false};
}

// Preserve each original root equation's instance path while taking its final
// value from the complete document graph (including local child equations).
export function reconcileStructureDimensions(graph,dimensions){
  return dimensions.map(dimension=>{
    const model=graph.models.find(row=>row.templatePath===dimension.templatePath&&row.configuration===dimension.configuration),target=model?.dimensions.find(row=>row.dimension===dimension.dimension);
    if(!target)throw new DefinitionError('结构根方程目标未进入完整文档图：'+dimension.reference);
    if(Object.hasOwn(dimension,'valueMm'))return {...dimension,valueMm:target.valueMm};
    const value=target.unit==='count'?target.value:target.valueMm;
    return {...dimension,value,...(Object.hasOwn(dimension,'changed')?{changed:Math.abs(value-dimension.sourceValue)>1e-7}:{})};
  });
}
