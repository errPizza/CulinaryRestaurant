#!/bin/sh
set -eu
sudo cp -n /opt/culinaryos-app/kiosk/openbox.xml /opt/culinaryos-app/kiosk/openbox.xml.antes-foco
sudo sed -i 's|<mouse/>|<mouse><context name="Client"><mousebind button="Left Middle Right" action="Press"><action name="Focus"/><action name="Raise"/></mousebind></context></mouse>|' /opt/culinaryos-app/kiosk/openbox.xml
