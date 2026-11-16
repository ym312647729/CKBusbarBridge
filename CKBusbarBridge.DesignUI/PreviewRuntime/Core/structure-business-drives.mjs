import {DefinitionError} from './expressions.mjs';
import {baseTemplatePaths,frameTemplatePaths} from './structure-sources.mjs';
import {prepareSmallOffsetAngles} from './small-offset-angle-drives.mjs';

const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'未唯一对应');return rows[0];};
const positive=(value,label)=>{if(!Number.isFinite(value)||value<=0)throw new DefinitionError(label+'必须为有效正数');return Math.fround(value);};
const canonical=name=>name.split('@').slice(0,-1).join('@');
const prefixMatches=(component,prefix)=>new RegExp('^'+prefix.replaceAll('.','\\.')+'(?:$|[-_.])').test(component.name.split('/').at(-1));

// These are the programmatic operations performed after the original template
// equations: FaceToFaceBaseDriver.BuildSidePanelRules/BuildSidePanelPlateVariables,
// FaceToFaceSmallOffsetFramePlanner.CreateCoverPlatePlans and
// FaceToFaceSmallOffsetSealPlatePlanner.CreatePlan. Never expose every variant.
// The input templates remain immutable; only this evaluation owns the copies.
export async function prepareStructureBusinessDrives(source,configurationName,globals,readSource,businessDrive){
  if(!businessDrive)return {template:source,readTemplate:readSource,evidence:[]};
  const copies=new Map(),evidence=[];
  const copy=async path=>{if(!copies.has(path))copies.set(path,structuredClone(path===source.source.path?source:await readSource(path)));return copies.get(path);};
  const read=async path=>copies.get(path)??readSource(path);
  const root=await copy(source.source.path),configuration=one(root.configurations.filter(row=>row.name===configurationName),'结构原配置');
  function setComponentState(owner,component,suppressed){
    if(component.suppression!==(suppressed?0:2))owner.businessComponentsChanged=true;
    evidence.push({operation:'component-suppression',component:component.name,suppressed,source:'original-programmatic-driver'});
    component.suppression=suppressed?0:2;
  }
  function folderComponents(owner,name){
    const folder=one(owner.features.filter(row=>row.type==='FtrFolder'&&row.name===name),'原结构文件夹 '+name),index=owner.features.indexOf(folder),members=[];
    let depth=1,closed=false;
    for(const feature of owner.features.slice(index+1)){
      if(feature.type==='FtrFolder'){
        if(feature.name.includes('___EndTag___')){if(--depth===0){closed=true;break;}}
        else depth++;
      }else if(feature.type==='Reference')members.push(one(owner.components.filter(row=>row.name.split('/').at(-1)===feature.name),'文件夹原组件 '+name+'/'+feature.name));
    }
    if(!closed||!members.length)throw new DefinitionError('原结构文件夹缺少完整成员：'+name);
    return members;
  }
  async function drive(component,values){
    const template=await copy(component.path),target=one(template.configurations.filter(row=>row.name===component.configuration),'结构子件原引用配置');
    for(const [name,value]of Object.entries(values)){
      positive(value,'结构驱动 '+component.name+'/'+name);
      const dimension=name.includes('@');
      if(dimension&&!target.features.some(feature=>feature.dimensions.some(row=>canonical(row.name)===name)))throw new DefinitionError('结构驱动缺少原尺寸：'+component.name+'/'+name);
      if(!dimension&&!target.equations.some(row=>row.globalVariable&&/^\s*"([^"]+)"\s*=/.exec(row.expression)?.[1]===name))throw new DefinitionError('结构驱动缺少原全局变量：'+component.name+'/'+name);
      const existing=target.equations.filter(row=>/^\s*"([^"]+)"\s*=/.exec(row.expression)?.[1]===name);
      if(existing.length>1)throw new DefinitionError('结构驱动方程目标重复：'+component.name+'/'+name);
      const equation={index:existing[0]?.index??target.equations.length,expression:JSON.stringify(name)+' = '+Math.fround(value),globalVariable:!dimension,disabled:false,businessDriveSource:'original-programmatic-driver'};
      if(existing.length)target.equations[target.equations.indexOf(existing[0])]=equation;else target.equations.push(equation);
      evidence.push({operation:'dimension-equation',templatePath:component.path,configuration:component.configuration,name,value:Math.fround(value),source:'original-programmatic-driver'});
    }
  }
  if(businessDrive.kind==='Base'){
    if(source.source.path!==baseTemplatePaths.Base||typeof businessDrive.hasElevatedBase!=='boolean')throw new DefinitionError('普通基座侧板计划缺少已核验的叠装身份');
    for(const [prefix,suppressed]of [['8GX.017.216',businessDrive.hasElevatedBase],['8GX.017.217',!businessDrive.hasElevatedBase]]){
      const panel=one(configuration.components.filter(row=>prefixMatches(row,prefix)),'普通基座原侧板 '+prefix);
      setComponentState(configuration,panel,suppressed);
      if(!suppressed)await drive(panel,{'板长':positive(globals['基座宽']-2,'基座侧板长'),'板宽':positive(globals['基座高']-globals['基座开孔高度']-2,'基座侧板宽')});
    }
  }else if(businessDrive.kind==='FaceToFaceSmallOffset032'){
    if(source.source.path!==frameTemplatePaths.FaceToFaceSmallOffset032)throw new DefinitionError('小偏移结构计划与原模板不一致');
    const width=positive(globals['基架宽度'],'小偏移基架宽'),length=positive(globals['基架长度'],'小偏移基架长'),height=positive(globals['基架高度'],'小偏移基架高'),doubleCover=width>1400;
    for(const [name,active]of [['单盖板',!doubleCover],['双盖板',doubleCover]]){
      const members=folderComponents(configuration,name),expected=active?(doubleCover?4:1):null;
      if(expected!==null&&members.length!==expected)throw new DefinitionError('小偏移盖板文件夹成员数与原计划不一致：'+name);
      for(const component of members){
        setComponentState(configuration,component,!active);if(!active)continue;
        // The suppressed 222 reference was captured with its old absolute path.
        // Resolve only this explicitly known template alias, never by proximity.
        if(prefixMatches(component,'8GX.017.222')){
          const path='模板/基架模板/小偏移基架/8GX.017.222.SLDPRT';
          if(!component.path.replaceAll('\\','/').endsWith('/基架模板/小偏移基架/8GX.017.222.SLDPRT'))throw new DefinitionError('小偏移单盖板原引用路径不匹配');
          const original=await readSource(path);if(original.source.path!==path)throw new DefinitionError('小偏移单盖板缓存身份不匹配');component.path=path;
          await drive(component,{'D1@草图1':length-2,'D2@草图1':width-2});
        }else if(prefixMatches(component,'8GX.017.227'))await drive(component,{'D1@草图1':length-2,'D2@草图1':width/2-2});
        else if(prefixMatches(component,'8GX.160.128'))await drive(component,{'D1@草图1':length-80,'D1@草图2':length});
        else throw new DefinitionError('小偏移盖板文件夹包含未知原组件：'+component.name);
      }
    }
    const child=one(configuration.components.filter(row=>prefixMatches(row,'6GX.355.032')),'小偏移原基架子装配体'),assembly=await copy(child.path),inner=one(assembly.configurations.filter(row=>row.name===child.configuration),'小偏移原子配置');
    const inlet=globals['进孔偏移方向'],outlet=globals['出孔偏移方向'],right=inlet===0&&outlet===1,left=inlet===1&&outlet===0;
    if(!right&&!left)throw new DefinitionError('小偏移基架进出孔偏移方向必须互为镜像');
    const inletWidth=Math.fround(2*globals['进孔偏移尺寸']),outletWidth=Math.fround(2*globals['出孔偏移尺寸']);
    if(!Number.isFinite(inletWidth)||!Number.isFinite(outletWidth)||inletWidth<0||outletWidth<0||height<=2)throw new DefinitionError('小偏移封板驱动尺寸无效');
    for(const [name,suppressed,prefix,variable,plateWidth]of [
      ['进左封板角钢',right||inletWidth<=2,'8GX.017.236','进孔偏移尺寸',inletWidth],
      ['进右封板角钢',left||inletWidth<=2,'8GX.017.236','进孔偏移尺寸',inletWidth],
      ['出左封板角钢',left||outletWidth<=2,'8GX.017.237','出孔偏移尺寸',outletWidth],
      ['出右封板角钢',right||outletWidth<=2,'8GX.017.237','出孔偏移尺寸',outletWidth]]){
      const members=folderComponents(inner,name),plate=one(members.filter(row=>prefixMatches(row,prefix)),'小偏移原侧封板 '+name);
      for(const component of members){if(component!==plate&&!prefixMatches(component,'8GX.160.167'))throw new DefinitionError('小偏移封板文件夹包含未知组件：'+component.name);setComponentState(inner,component,suppressed||component!==plate&&plateWidth<=70);}
      if(!suppressed)await drive(plate,{'基架高度':height,[variable]:plateWidth});
    }
    await prepareSmallOffsetAngles(configuration,inner,height,right,inletWidth,outletWidth,{copies,readSource,drive,evidence,templateNotches:businessDrive.suppressTemplateAngleNotches??null});
  }else throw new DefinitionError('未定义的结构原程序驱动：'+businessDrive.kind);
  return {template:root,readTemplate:read,evidence};
}
