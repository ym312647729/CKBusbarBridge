import {DefinitionError} from './expressions.mjs';
import {validateOriginalCabinetInputs,editOriginalCabinetInput} from './original-cabinet.mjs';
import {layoutFromSideToSideBase1} from './insulator-planning.mjs';

// Original pure planners use two distinct rounding modes. Keep their operation
// order, including the threshold comparison after rounding the total height.
const roundAway=value=>Math.sign(value)*Math.round(Math.abs(value)*1000)/1000;
const roundEven=value=>{const scaled=value*1000,lower=Math.floor(scaled),fraction=scaled-lower;return (fraction===.5?(lower%2===0?lower:lower+1):Math.round(scaled))/1000;};
const finite=(value,label,allowZero=false)=>{if(!Number.isFinite(value)||(allowZero?value<0:value<=0))throw new DefinitionError(label+(allowZero?'必须为有限非负数':'必须为有限正数'));return value;};
const same=(values,defaults)=>values.every((value,index)=>roundAway(value)===defaults[index]);
const generalValue=(values,name)=>values['General.'+name];
function barThickness(specification){const parts=String(specification??'').trim().replace(/[×*X]/g,'x').split('x').filter(Boolean),parsed=parts.length===2?Number(parts[1].trim()):NaN;return Number.isFinite(parsed)&&parsed>0?parsed:8;}
function barWidth(specification){const matches=String(specification??'').match(/\d+(?:\.\d+)?/g),parsed=matches?.length>=2?Number(matches[matches.length>=3?1:0]):NaN;return Number.isFinite(parsed)&&parsed>0?parsed:null;}
function insulationInput(values,scenario){return {phaseSpacing:generalValue(values,scenario==='face-to-face'?'PhaseSpacing':'ExposedBusbarPhaseSpacing'),insulatorHeight:generalValue(values,'InsulatorHeight'),singlePhaseRootCount:generalValue(values,'SinglePhaseRootCount'),busbarSpec:generalValue(values,'BusbarSpec')};}
function validateInsulator(input){finite(input.phaseSpacing,'伸出排相间距');finite(input.insulatorHeight,'绝缘子及支架高度');if(input.insulatorHeight<=25)throw new DefinitionError('绝缘子及支架高度必须大于25 mm');if(!Number.isInteger(input.singlePhaseRootCount)||input.singlePhaseRootCount<1||input.singlePhaseRootCount>4)throw new DefinitionError('单相根数必须为1至4');}

export function minimumBaseOpeningHeight(input,insulationDistance,phaseChange){
  finite(input.insulatorHeight,'绝缘子及支架高度');const thickness=barThickness(input.busbarSpec);finite(insulationDistance,'绝缘距离');if(!Number.isInteger(input.singlePhaseRootCount)||input.singlePhaseRootCount<=0)throw new DefinitionError('单相根数必须为正整数');
  return roundEven(41.5+input.insulatorHeight+(phaseChange?insulationDistance+5+(3*input.singlePhaseRootCount+.5)*thickness+40+insulationDistance:(2*input.singlePhaseRootCount-1)*thickness+insulationDistance+40));
}
function modelPlan(kind,height,width,depth,l1,l2,l3,openingHeight,hasElevatedBase,qualifier){
  const ordinary=kind==='Base',values=(ordinary?[height,width,depth,l1,l2,l3,openingHeight]:[height,width,depth,l1,l2,l3]).map(roundAway),globalVariables=Object.fromEntries((ordinary?['基座高','基座宽','基座深','L1','L2','L3','基座开孔高度']:['高度','宽度','深度','L1','L2','L3']).map((name,index)=>[name,values[index]]));
  const parts=[kind,...values.map(value=>value.toFixed(3))];if(hasElevatedBase!==null)parts.push('HasElevatedBase='+(hasElevatedBase?'True':'False'));if(qualifier)parts.push(qualifier);
  return {kind,height:values[0],width:values[1],depth:values[2],l1:values[3],l2:values[4],l3:values[5],isTemplateDefault:same(values,ordinary?[1000,800,500,100,250,350,500]:[400,800,500,100,250,350]),cacheKey:parts.join('|'),globalVariables,hasElevatedBase};
}

function standalonePosition(values,candidate,input){
  const path=candidate.path,get=name=>values[path+'.'+name];
  for(const name of ['BaseHeight','FixedHoleSpacing','L1','L2','L3','BaseOpeningHeight'])finite(get(name),candidate.positionName+' '+name);finite(get('InsulatorPosition'),'绝缘子定位高度',true);validateInsulator(input);
  const totalHeight=roundAway(get('BaseHeight')),hasElevatedBase=totalHeight>1400;if(hasElevatedBase)finite(get('ExtensionBaseHeight'),'延长基座高');
  const extensionHeight=hasElevatedBase?roundAway(get('ExtensionBaseHeight')):0,height=roundAway(totalHeight-extensionHeight);finite(height,'普通基座计算高度');
  const width=roundAway(finite(values[candidate.cabinet+'.CabinetWidth'],'所属柜体宽')),depth=roundAway(get('FixedHoleSpacing')+41.5);if(width<=20)throw new DefinitionError('基座宽必须大于20 mm');
  const absolute=roundAway(get('InsulatorPosition'));if(hasElevatedBase&&absolute===extensionHeight)throw new DefinitionError('绝缘子定位高度不能等于增高基座高度');
  const targetBaseKind=hasElevatedBase&&absolute<extensionHeight?'ElevatedBase':'Base',placementHeight=roundAway(targetBaseKind==='ElevatedBase'?absolute:absolute-extensionHeight),placementDimensionName=targetBaseKind==='ElevatedBase'?'D1@草图2':'D1@绝缘子安装坐标系草图';finite(placementHeight,'绝缘子局部定位高度',true);
  const globalVariables={'排厚':roundAway(barThickness(input.busbarSpec)),'相间距':roundAway(input.phaseSpacing),'绝缘子高度':roundAway(input.insulatorHeight-25),'支架长度':roundAway(width-20)},insulator={targetBaseKind,placementDimensionName,placementHeight,globalVariables,singlePhaseRootCount:input.singlePhaseRootCount,busbarSpec:input.busbarSpec},qualifier='Insulator='+placementDimensionName+','+[placementHeight,globalVariables['排厚'],globalVariables['相间距'],globalVariables['绝缘子高度']].map(value=>value.toFixed(3)).join(',')+',Roots='+input.singlePhaseRootCount;
  const args=[width,depth,get('L1'),get('L2'),get('L3'),get('BaseOpeningHeight')];
  return {positionName:candidate.positionName,rootCoordinateName:candidate.rootCoordinateName,baseModel:modelPlan('Base',height,...args,hasElevatedBase,targetBaseKind==='Base'?qualifier:null),elevatedBaseModel:hasElevatedBase?modelPlan('ElevatedBase',extensionHeight,...args,null,targetBaseKind==='ElevatedBase'?qualifier:null):null,insulator,hasElevatedBase};
}

function sideModel(kind,variables,defaults){const globalVariables=Object.fromEntries(Object.entries(variables).map(([name,value])=>[name,roundAway(value)]));return {kind,globalVariables,isTemplateDefault:same(Object.values(globalVariables),defaults)};}
function sidePosition(values,candidate,input){
  const path=candidate.path,get=name=>values[path+'.'+name],isACabinet=candidate.cabinet==='ACabinet',width=finite(values[candidate.cabinet+'.CabinetWidth'],'柜宽'),leftRight=values['General.CabinetTopOpeningMode']==='LeftRight',basePath=path+(leftRight?'.Base1':'.Base2'),base=name=>values[basePath+'.'+name],baseWidth=leftRight?get('L3')*2+100:base('BaseWidth');
  for(const [name,value]of Object.entries({'基座高':base('BaseHeight'),'基座宽':baseWidth,'基座深':width,L1:get('L1'),L2:get('L2'),L3:get('L3'),'基座开孔高度':base('BaseOpeningHeight')}))finite(value,name);
  const variables=height=>({'基座高':height,'基座宽':baseWidth,'基座深':width,L1:get('L1'),L2:get('L2'),L3:get('L3'),'基座开孔高度':base('BaseOpeningHeight')});
  const placement=(hostModelKind,hostCoordinateName,placementDimensionName,placementHeight,bracketHostSpan,layout=null)=>({hostModelKind,hostCoordinateName,childCoordinateName:'安装坐标系',placementDimensionName,placementHeight,busbarSpec:input.busbarSpec,phaseSpacing:input.phaseSpacing,insulatorHeight:input.insulatorHeight,bracketHostSpan,singlePhaseRootCount:input.singlePhaseRootCount,layout});
  if(leftRight){
    const positions=[1,2,3].map(index=>finite(base('InsulatorPosition'+index),'绝缘子定位'+index,true));if(!(positions[0]<positions[1]&&positions[1]<positions[2]))throw new DefinitionError('绝缘子定位1、2、3必须由低到高严格递增');
    const nativeLayout=layoutFromSideToSideBase1(width-20,input.phaseSpacing,get('FixedHoleSpacing')),layout={bracketLength:nativeLayout.bracketLength,distances:nativeLayout.distances,offset:nativeLayout.offset,identity:nativeLayout.distances.map(value=>roundEven(value).toFixed(3).replace(/\.?0+$/,'')).join('_')},suffix=isACabinet?'A':'B';
    return {positionName:candidate.positionName,isACabinet,baseModel:sideModel('Base1',variables(base('BaseHeight')),[1300,800,500,100,250,350,800]),elevatedBaseModel:null,baseInstallationCoordinateName:'安装坐标系',insulators:positions.map((position,index)=>placement('Base1','绝缘子安装坐标系'+(index+1)+'('+suffix+')','D'+(index+1)+'@绝缘子安装坐标系草图'+suffix,position,width,layout)),hasElevatedBase:false};
  }
  const elevatedDepth=finite(get('FixedHoleSpacing')+41.5,'增高基座深'),insulationDistance=finite(generalValue(values,'InsulationDistance'),'绝缘距离'),bar=barWidth(input.busbarSpec);if(bar===null)throw new DefinitionError('无法从铜排规格解析排宽，不能校验基座2宽');
  const minimumWidth=2*input.phaseSpacing+bar+2*(19.5+insulationDistance+5);if(baseWidth<minimumWidth)throw new DefinitionError('基座2宽不能小于 '+roundAway(minimumWidth));
  const elevatedHeight=finite(base('ElevatedBaseHeight'),'增高基座高度');if(elevatedHeight<250)throw new DefinitionError('增高基座高度不能小于250');if(base('BaseHeight')<=elevatedHeight)throw new DefinitionError('组合基座高必须大于增高基座高度');
  const position=finite(base('InsulatorPosition'),'绝缘子定位',true);if(position>elevatedHeight)throw new DefinitionError('绝缘子定位不能大于增高基座高');
  return {positionName:candidate.positionName,isACabinet,baseModel:sideModel('Base2',{...variables(base('BaseHeight')-elevatedHeight),'挡板距边距离':(baseWidth-elevatedDepth)/2},[1000,800,800,100,250,350,500,150]),elevatedBaseModel:sideModel('ElevatedBase',{'高度':elevatedHeight,'宽度':width,'深度':elevatedDepth,L1:get('L1'),L2:get('L2'),L3:get('L3')},[400,800,500,100,250,300]),baseInstallationCoordinateName:isACabinet?'安装坐标系(A)':'安装坐标系(B)',insulators:[placement('ElevatedBase','绝缘子安装坐标系','D1@草图2',position,width)],hasElevatedBase:true};
}

export function planCabinetBases(scenario,record){
  let normalized=validateOriginalCabinetInputs({[scenario]:record})[scenario];const input=insulationInput(normalized.values,scenario),candidates=(scenario==='face-to-wall'?['Cabinet']:['ACabinet','BCabinet']).flatMap(cabinet=>['FrontHole','BackHole'].map(side=>({cabinet,path:cabinet+'.'+side,positionName:(cabinet==='Cabinet'?'':cabinet==='ACabinet'?'A':'B')+'柜'+(side==='FrontHole'?'前':'后'),rootCoordinateName:scenario==='face-to-wall'?'柜'+(side==='FrontHole'?'前':'后')+'安装坐标系':(cabinet==='ACabinet'?'A':'B')+'柜'+(side==='FrontHole'?'前':'后')})));
  const a=generalValue(normalized.values,'ACabinetExposedBusbarPhaseSequence'),b=generalValue(normalized.values,'BCabinetExposedBusbarPhaseSequence'),phaseChange=scenario==='face-to-face'?!!a?.trim()&&!!b?.trim()&&a===b:scenario==='side-to-side'&&a!==b,side=scenario==='side-to-side';
  // Base2 换相在基座内完成；桥体沿用普通排布的开孔高度下限。
  const bridgePhaseChange=phaseChange&&(!side||normalized.values['General.CabinetTopOpeningMode']==='LeftRight');
  if(side){if(typeof input.busbarSpec!=='string'||!input.busbarSpec.trim())throw new DefinitionError('铜排规格不能为空');validateInsulator(input);}
  const minimum=minimumBaseOpeningHeight(input,generalValue(normalized.values,'InsulationDistance'),bridgePhaseChange);
  const internalSet=(path,value)=>{const manual=normalized.manual;normalized=editOriginalCabinetInput(normalized,scenario,path,value);normalized.manual=manual;};
  for(const candidate of candidates){
    if(!normalized.values[candidate.path+'.IsEnabled'])continue;
    const basePath=candidate.path+(side?(normalized.values['General.CabinetTopOpeningMode']==='LeftRight'?'.Base1':'.Base2'):''),openingPath=basePath+'.BaseOpeningHeight',heightPath=basePath+'.BaseHeight';
    let opening=finite(normalized.values[openingPath],'基座开孔高度');
    if(bridgePhaseChange){opening=Math.max(roundEven(opening),minimum+10);internalSet(openingPath,opening);if(normalized.values[heightPath]<=opening){const height=roundEven(opening+1);internalSet(heightPath,height);if(!side)internalSet(basePath+'.ExtensionBaseHeight',height>1400?height-1000:0);}}
    else if(minimum-opening>1.0)throw new DefinitionError(candidate.positionName+'普通排布的基座开孔高度不能小于 '+minimum.toFixed(1)+' mm');
  }
  const enabled=candidates.filter(candidate=>normalized.values[candidate.path+'.IsEnabled']),plans=enabled.map(candidate=>side?sidePosition(normalized.values,candidate,input):standalonePosition(normalized.values,candidate,input));
  return {version:'original-cabinet-base-planning/1',scenario,record:normalized,plans,phaseChange,minimumOpeningHeight:minimum,geometryInstalled:false};
}
