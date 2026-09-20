#!/usr/bin/env python3
"""Exercise display selection and validation without changing a real monitor."""
import importlib.util
import pathlib
import copy
import unittest
from unittest.mock import patch

path=pathlib.Path(__file__).resolve().parents[2]/'src/Xur.Agent/StationDisplay.py'
spec=importlib.util.spec_from_file_location('display',path)
display=importlib.util.module_from_spec(spec)
spec.loader.exec_module(display)

def output():
    return {'name':display.OUTPUT,'connected':True,'enabled':True,'currentModeId':'0',
            'modes':[{'id':'0','size':{'width':1920,'height':1080},'refreshRate':60}]}

class Modes(unittest.TestCase):
    def test_sunshine_offscreen_environment_is_not_inherited(self):
        with patch.dict(display.os.environ,{'QT_QPA_PLATFORM':'offscreen','WAYLAND_DISPLAY':'wayland-3'}),patch.object(display.subprocess,'run') as run:
            run.return_value.returncode=0;run.return_value.stdout='{}'
            display.run('--json')
            self.assertEqual(run.call_args.kwargs['env']['QT_QPA_PLATFORM'],'wayland')
            self.assertEqual(run.call_args.kwargs['env']['WAYLAND_DISPLAY'],'wayland-3')

    def test_invalid_request_never_runs_command(self):
        with patch.object(display,'run') as run:
            for request in [(0,1080,60),(1921,1080,60),(7682,1080,60),(1920,1080,0),('1;id',1080,60)]:
                with self.assertRaises(ValueError):display.resize(*request)
            run.assert_not_called()

    def test_only_xur_virtual_output(self):
        import json
        physical=output();physical['name']='HDMI-A-1'
        with patch.object(display,'run',return_value=json.dumps({'outputs':[physical]})) as run:
            with self.assertRaises(RuntimeError):display.resize(1280,720,60)
            self.assertEqual(run.call_args_list,[unittest.mock.call('--json')])

    def test_custom_mode_applied_and_verified(self):
        state=output();calls=[]
        def run(command):
            calls.append(command)
            if '.addCustomMode.' in command:
                state['modes'].append({'id':'1','size':{'width':1280,'height':800},'refreshRate':90})
            elif '.mode.' in command:state['currentModeId']='1'
        with patch.object(display,'observe',side_effect=lambda:copy.deepcopy(state)),patch.object(display,'run',side_effect=run):
            result=display.resize(1280,800,90)
            self.assertEqual(result['currentModeId'],'1')
            self.assertEqual(calls,['output.Virtual-Xur-Stream.addCustomMode.1280.800.90000.full','output.Virtual-Xur-Stream.mode.1'])
            calls.clear();display.resize(1280,800,90)
            self.assertEqual(calls,['output.Virtual-Xur-Stream.mode.1'])

    def test_driver_rejection_retains_old_mode(self):
        state=output()
        with patch.object(display,'observe',return_value=state),patch.object(display,'run') as run:
            with self.assertRaises(RuntimeError):display.resize(2560,1440,120)
            self.assertEqual(run.call_count,1)
            self.assertEqual(state['currentModeId'],'0')

    def test_no_false_success_when_mode_not_applied(self):
        state=output();state['modes'].append({'id':'1','size':{'width':1280,'height':720},'refreshRate':60})
        with patch.object(display,'observe',return_value=state),patch.object(display,'run'):
            with self.assertRaises(RuntimeError):display.resize(1280,720,60)

    def test_kde_cvt_nominal_rate_uses_closest_existing_mode(self):
        state=output();state['currentModeId']='2'
        for id,rate in [('1',29.6439990997),('2',29.8349990845),('3',60)]:
            state['modes'].append({'id':id,'size':{'width':1280,'height':800},'refreshRate':rate})
        with patch.object(display,'observe',return_value=state),patch.object(display,'run') as run:
            display.resize(1280,800,30)
            run.assert_called_once_with('output.Virtual-Xur-Stream.mode.2')

unittest.main()
