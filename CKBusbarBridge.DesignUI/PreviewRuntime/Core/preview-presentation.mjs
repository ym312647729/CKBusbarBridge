// Presentation preferences are independent of design dimensions and geometry.
export const parameterModes=['cabinet','frame','insulator','copper'];
export const currentPresentationVersion='studio-opaque-grid/1';
export function normalizePreviewSettings(settings){
  if(settings.presentationVersion===currentPresentationVersion)return settings;
  return {...settings,presentationVersion:currentPresentationVersion,transparency:0,frameTransparency:0,roofTransparency:.5,beamTransparency:.5,partTransparency:{}};
}
export function previewPartRole(object,source={}){
  if(source.previewRole==='fixed-copper')return 'copper';
  const file=(source.path??object.templatePath??'').split('/').at(-1).toLowerCase();
  if(file==='地面.sldprt')return 'ground';
  if(/^(?:屋顶|棚顶)\.sldprt$/.test(file))return 'roof';
  if(/^(?:横梁|梁)\.sldprt$/.test(file))return 'beam';
  if(object.structureSupportBinding||object.insulatorBinding||object.category==='insulator')return 'insulator';
  if(object.category==='frame'||object.frameBinding)return 'frame';
  if(object.category==='copper')return 'copper';
  return object.category;
}
export function previewPartKey(object,source={}){
  return JSON.stringify([object.id,source.componentChain??[],source.path??object.templatePath,source.bodyIndex??0]);
}
export function partTransparency(settings,object,source={}){
  const role=previewPartRole(object,source),key=previewPartKey(object,source);
  if(role==='ground')return 0;
  const override=settings.partTransparency?.[key];
  if(override!==undefined)return override;
  if(['roof','beam'].includes(role))return settings[role+'Transparency']??.5;
  if(role==='frame')return settings.frameTransparency??0;
  return settings.transparency??0;
}
export function canSelectPreviewPart(mode,object,source={}){
  if(!object)return false;
  const role=previewPartRole(object,source);
  if(role==='ground')return false;
  if(mode==='frame')return role==='frame';
  if(mode==='insulator')return role==='insulator'||['bracket','pad'].includes(role);
  if(mode==='copper')return role==='copper';
  if(mode==='cabinet')return !!object.cabinetBinding||!!object.baseBinding||object.automaticCopperBinding?.module==='cabinet'||['cabinet','base','raisedBase','wall'].includes(role);
  return false;
}
export function normalizeDesignWorkflow(workflow={}){
  const step=!workflow.step||['cabinet','base'].includes(workflow.step)?'cabinet':'bridge';
  return {...workflow,step,completed:[...new Set((workflow.completed??[]).map(key=>['cabinet','base'].includes(key)?'cabinet':'bridge'))]};
}
