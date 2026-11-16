import {cutCopperPreviewHoles} from '../../Core/original-copper-holes.mjs';
import {createKernelFromModule} from './kernel.js';
import {listParameters} from '../../Core/geometry.mjs';
import {evaluatePreview} from '../../Core/preview-quality.mjs';
import {collectTemplateInterfaces} from '../../Core/template-interfaces.mjs';
import {collectTemplatePlanes} from '../../Core/template-planes.mjs';
import {hangerFrameDefinition} from '../../Core/hanger-draft.mjs';
import {deriveHangerAssembly} from '../../Core/hanger-assembly.mjs';
import {hangerTemplateReference} from '../../Core/hanger-installation.mjs';
import {evaluateCopperObject} from '../../Core/original-copper.mjs';
import {deriveCabinetScene} from '../../Core/cabinet-scene.mjs';
import {collectOriginalRoofPlanes} from '../../Core/original-roof-planes.mjs';
import {evaluateInsulatorDraftPreview,evaluateInsulatorDesign} from '../../Core/insulator-preview.mjs';
import {deriveStructureAssembly} from '../../Core/structure-assembly.mjs';
import {basePreviewParameters} from '../../Core/base-binding.mjs';
import {appendFixedCabinetCopper} from '../../Core/cabinet-fixed-copper.mjs';
import {PreviewMeshCache} from './preview-mesh-cache.js';
const definitions=new Map(),definitionReadMetrics=new Map();
const previewMeshCache=new PreviewMeshCache();
let resolveCompiledKernel,rejectCompiledKernel,kernelCompilationMs;
const compiledKernelReady=new Promise((resolve,reject)=>{resolveCompiledKernel=resolve;rejectCompiledKernel=reject;});
const kernelReady=compiledKernelReady.then(createKernelFromModule);kernelReady.catch(()=>{});
async function loadTemplate(templatePath){
  if(!templatePath||templatePath.includes('..')||templatePath.includes(':')||templatePath.startsWith('/'))throw new Error('模板引用越过本地模板目录：'+templatePath);
  if(!definitions.has(templatePath)){
    const pending=(async()=>{const started=performance.now(),response=await fetch('../../templates/'+templatePath+'.virtual.json'),headersReceived=performance.now();if(!response.ok)throw new Error('真实模板定义尚未采集：'+templatePath);const text=await response.text(),bodyReceived=performance.now(),definition=JSON.parse(text),parsed=performance.now();definitionReadMetrics.set(templatePath,{path:templatePath,started,fetchHeadersMs:headersReceived-started,bodyReadMs:bodyReceived-headersReceived,jsonParseMs:parsed-bodyReceived,totalReadMs:parsed-started});return definition;})();
    definitions.set(templatePath,pending);pending.catch(()=>{if(definitions.get(templatePath)===pending)definitions.delete(templatePath);});
  }
  return definitions.get(templatePath);
}
self.onmessage=async event=>{
  if(event.data.type==='initialize-kernel'){kernelCompilationMs=event.data.initializationMs;resolveCompiledKernel(event.data.module);return;}
  if(event.data.type==='initialize-kernel-error'){rejectCompiledKernel(new Error(event.data.message));return;}
  const {object,revision,requestId,signature,settings,purpose}=event.data;
  const started=performance.now(),meshCacheBefore=previewMeshCache.snapshot(),meshCacheMetrics=()=>{const current=previewMeshCache.snapshot();return {...current,hits:current.hits-meshCacheBefore.hits,misses:current.misses-meshCacheBefore.misses};};let parameters=[],coordinates={interfaces:[],diagnostics:[]},roofPlanes={complete:false,planes:[],diagnostics:[]},hangerFrame=null,interfaceReadMs=0,driveMs=0,derivedGeometry=null,businessParameters=null,fixedCopper=null;let result;
  try{
    if(['free-copper','original-copper'].includes(object.kind)){
      const waitStart=performance.now(),kernel=await kernelReady,kernelWaitMs=performance.now()-waitStart,evaluationStart=performance.now();result=evaluateCopperObject(kernel,object,settings);result=cutCopperPreviewHoles(kernel,result,object,settings);
      const evaluationMs=performance.now()-evaluationStart,meshStart=performance.now(),meshes=result.solids.map((solid,index)=>{
        const source=result.solidSources?.[index];
        return source?.localMesh?{...source.localMesh,partSource:{...source,localMesh:undefined}}:previewMeshCache.convert(solid);
      }),meshMs=performance.now()-meshStart,triangleCount=meshes.reduce((sum,m)=>sum+m.triangleCount,0),overBudget=triangleCount>(settings.objectTriangleBudget??80000);
      const transfers=meshes.flatMap(mesh=>[mesh.positions.buffer,mesh.indices.buffer,mesh.patches.quads.buffer,mesh.patches.triangles.buffer]);
      self.postMessage({revision,requestId,purpose,objectId:object.id,signature,ok:true,finishedAt:performance.timeOrigin+performance.now(),meshes,parameters:result.parameters,interfaces:[],interfaceDiagnostics:[],roofPlanes:{complete:false,planes:[],diagnostics:[]},derivedGeometry:result.derivedGeometry,preview:{...result.preview,triangleCount},previewDisplayMode:overBudget?'outline':'solid',qualityWarning:overBudget?'铜排超过预览预算，保留完整轮廓':null,schematic:[],deferred:[],metrics:{readMs:0,driveMs:0,parameterMs:0,kernelWaitMs,kernelCompilationMs,definitionReads:[],cutFeatureMs:result.geometryTimings.cutFeatureMs,constructionFeatureMs:result.geometryTimings.otherFeatureMs,evaluationAndHolesMs:evaluationMs,meshMs,meshCache:meshCacheMetrics(),interfaceReadMs:0,workerMs:performance.now()-started,triangleCount}},transfers);
      return;
    }
    const cachedDefinition=definitions.has(object.templatePath),readStart=performance.now();let kernelWaitMs;
    const [kernel,sourceTemplate]=await Promise.all([kernelReady.then(kernel=>{kernelWaitMs=performance.now()-readStart;return kernel;}),loadTemplate(object.templatePath)]);let template=sourceTemplate,templateLoader=loadTemplate;
    const rootDefinitionRead=cachedDefinition?{cached:true,totalReadMs:0}:{...definitionReadMetrics.get(object.templatePath),cached:false},readMs=rootDefinitionRead.totalReadMs;
    if(object.sourceSha256&&template.source.sha256!==object.sourceSha256)throw new Error('模板签名已变化，请核对来源后重新关联');
    if(object.cabinetBinding){const driveStart=performance.now(),derived=await deriveCabinetScene(sourceTemplate,object.configuration,object.cabinetBinding.scenario,object.cabinetSceneInput,loadTemplate);template=derived.template;templateLoader=derived.loadTemplate;businessParameters=derived.parameters;fixedCopper=derived.fixedCopper;roofPlanes=await collectOriginalRoofPlanes(derived,object.configuration);driveMs=performance.now()-driveStart;derivedGeometry={kind:'cabinet-scene',stage:'cabinet-with-fixed-outgoing-copper',fixedCopper,diagnostics:derived.diagnostics.filter(row=>!fixedCopper||!/伸出.*尚未生成|伸出.*待求解/.test(row.message)),drivenSubsystems:derived.evidence.filter(row=>row.kind).map(row=>({kind:row.kind,modelId:row.modelId,modelCount:row.models?.length})),nativeGeometryEquivalenceVerified:false};}
    if(object.hangerDimensions){
      const reference=hangerTemplateReference(sourceTemplate,object.configuration),saved=object.hangerBinding?.templateReference;
      if(!saved||saved.featureId!==reference.featureId||saved.sourceSha256!==reference.sourceSha256||!Array.isArray(saved.localTransform)||saved.localTransform.length!==16||saved.localTransform.some((value,index)=>Math.abs(value-reference.localTransform[index])>1e-7))throw new Error('吊杆保存的原始安装坐标系与当前定义不符');
      const driveStart=performance.now(),derived=await deriveHangerAssembly(sourceTemplate,object.configuration,{'宽度':object.hangerDimensions.width,'角钢高度':object.hangerDimensions.angleLength},loadTemplate);template=derived.template;templateLoader=derived.loadTemplate;driveMs=performance.now()-driveStart;derivedGeometry={kind:'hanger',dimensions:object.hangerDimensions,evidence:derived.evidence,nativeGeometryEquivalenceVerified:false};
    }
    if(object.frameBinding||object.baseBinding){const driveStart=performance.now(),input=object.structurePlanInput,derived=await deriveStructureAssembly(sourceTemplate,object.configuration,input.globalVariables,loadTemplate,{rootDimensions:input.rootDimensions,businessDrive:input.businessDrive});template=derived.template;templateLoader=derived.loadTemplate;businessParameters=object.baseBinding?basePreviewParameters(input,template.configurations.find(row=>row.name===object.configuration)):derived.parameters;driveMs=performance.now()-driveStart;derivedGeometry={kind:object.baseBinding?'base-model':'frame-model',installationStatus:input.installationStatus,sourceName:input.sourceName,diagnostics:derived.diagnostics,drivenDocuments:derived.evidence.length,nativeModelValidationRequired:false};}
    parameters=listParameters(template,object.configuration);
    if(purpose!=='interaction-lod'&&!object.insulatorBinding&&!object.structureSupportBinding){
      const interfaceStart=performance.now();
      try{coordinates=await collectTemplateInterfaces(template,object.configuration,templateLoader,object.parameters);}catch(error){coordinates.diagnostics.push({message:error.message});}
      if(!object.cabinetBinding)roofPlanes=await collectTemplatePlanes(template,object.configuration,templateLoader,object.parameters);
      hangerFrame=hangerFrameDefinition(object.templatePath,parameters);interfaceReadMs=performance.now()-interfaceStart;
    }
    const evaluationStart=performance.now();
    result=object.structureSupportBinding?await evaluateInsulatorDesign(kernel,object.insulatorPlanInput,loadTemplate,settings):object.insulatorBinding?await evaluateInsulatorDraftPreview(kernel,object.insulatorPlanInput,loadTemplate,settings):await evaluatePreview(kernel,template,object.configuration,object.parameters,settings,templateLoader);
    if(fixedCopper)appendFixedCabinetCopper(kernel,result,fixedCopper,settings);
    if(object.insulatorBinding)derivedGeometry={kind:'insulator-group',plans:result.recipes,evidence:result.evidence,nativeModelValidationRequired:false};
    parameters=businessParameters??result.parameters;
    if(object.structureSupportBinding){const input=object.insulatorPlanInput;parameters=input.layoutMode?[]:[{name:'General.InsulatorHeight',label:'绝缘子及支架高度',value:input.bodyHeight+25+(input.useLBracket?6:0),editable:true,annotationEdges:[[[0,-25,0],[0,input.bodyHeight+(input.useLBracket?6:0),0]]],source:'original-general-insulator-group'}];}
    const evaluationMs=performance.now()-evaluationStart,meshStart=performance.now();
    const meshes=result.solids.map((solid,index)=>{
      const source=result.solidSources?.[index]??{path:object.templatePath,componentChain:[],bodyIndex:index};
      return {...(source.localMesh??previewMeshCache.convert(solid)),partSource:{...source,localMesh:undefined}};
    }),meshMs=performance.now()-meshStart;
    const triangleCount=meshes.reduce((sum,mesh)=>sum+mesh.triangleCount,0);
    const partitionWarnings=meshes.map(mesh=>mesh.planarPartitions?.warning).filter(Boolean),partitionOverBudget=triangleCount>(settings.objectTriangleBudget??80000),newPartitionLimit=partitionOverBudget&&result.previewDisplayMode!=='outline';
    result.preview={...result.preview,nativeTriangleCount:result.preview.triangleCount,triangleCount,equivalentTriangleCount:meshes.reduce((sum,mesh)=>sum+mesh.equivalentTriangleCount,0),planarPartitions:{faces:meshes.reduce((sum,mesh)=>sum+(mesh.planarPartitions?.faces??0),0),holes:meshes.reduce((sum,mesh)=>sum+(mesh.planarPartitions?.holes??0),0),localFrames:meshes.reduce((sum,mesh)=>sum+(mesh.planarPartitions?.localFrames??0),0),rejectedFaces:meshes.reduce((sum,mesh)=>sum+(mesh.planarPartitions?.rejectedFaces??0),0)}};
    if(partitionOverBudget)result.previewDisplayMode='outline';
    const precisionLimitedFaces=meshes.reduce((sum,mesh)=>sum+(mesh.planarPartitions?.precisionLimitedFaces??0),0);if(precisionLimitedFaces)result.preview.planarPartitions.precisionLimitedFaces=precisionLimitedFaces;
    result.qualityWarning=[result.qualityWarning,...partitionWarnings,newPartitionLimit?'兼容提交面片超出对象预算，局部以轮廓显示':null].filter(Boolean).join('；')||null;
    const transfers=meshes.flatMap(mesh=>[mesh.positions.buffer,mesh.indices.buffer,mesh.patches.quads.buffer,mesh.patches.triangles.buffer]);
    const definitionReads=[...definitionReadMetrics.values()].filter(row=>row.started>=started),finishedAt=performance.timeOrigin+performance.now();
    self.postMessage({revision,requestId,purpose,objectId:object.id,signature,ok:true,finishedAt,meshes,parameters,interfaces:coordinates.interfaces,interfaceDiagnostics:coordinates.diagnostics,roofPlanes,hangerFrame,derivedGeometry,qualityWarning:result.qualityWarning,preview:result.preview,previewDisplayMode:result.previewDisplayMode,schematic:result.schematic??[],deferred:result.deferred??[],metrics:{readMs,driveMs,kernelWaitMs,kernelCompilationMs,rootDefinitionRead,definitionReads,parameterMs:result.parameterMs,cutFeatureMs:result.geometryTimings?.cutFeatureMs??0,constructionFeatureMs:result.geometryTimings?.otherFeatureMs??0,evaluationAndHolesMs:evaluationMs-result.parameterMs,meshMs,meshCache:meshCacheMetrics(),interfaceReadMs,workerMs:performance.now()-started,triangleCount}},transfers);
  }catch(error){self.postMessage({revision,requestId,purpose,objectId:object.id,signature,ok:false,error:error.message,source:{objectId:object.id,templatePath:object.templatePath,configuration:object.configuration,...error.source},detail:error.stack,parameters,interfaces:coordinates.interfaces,interfaceDiagnostics:coordinates.diagnostics,roofPlanes,hangerFrame,metrics:{driveMs,interfaceReadMs,workerMs:performance.now()-started}});}
  finally{result?.solids.forEach(solid=>solid.delete());}
};
